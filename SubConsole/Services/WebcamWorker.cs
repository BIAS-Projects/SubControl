using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Gst;
using Microsoft.Extensions.Logging;
using SysTask = System.Threading.Tasks.Task;

namespace SubConsole.Services
{
    public class WebcamWorker : IAsyncDisposable
    {
        private readonly Gst.Device _device;
        private readonly string _deviceName;
        private readonly string _host;
        private readonly int _port;
        private readonly ILogger _logger;

        private Pipeline? _pipeline;
        private CancellationTokenSource? _cts;
        private SysTask? _pipelineTask;
        private readonly SemaphoreSlim _semaphore = new(1, 1);
        private volatile bool _pipelineError;

        // Cached result of a successful caps probe so restarts skip re-probing.
        // Re-probing after a -5 error opens the device a second time while MF
        // hasn't fully released it, which triggers the same -5 on the real pipeline.
        private string? _cachedCaps;
        private bool _cachedNeedsGrayConvert;

        // Set from the bus thread when GST_FLOW_ERROR (-5) is seen so the
        // restart loop can apply a longer back-off before retrying.
        private volatile bool _deviceBusy;

        // Process-wide semaphore: only one camera probes at a time.
        // Concurrent probe pipelines open multiple devices simultaneously and
        // cause KS/MF driver contention that makes all probes fail.
        private static readonly SemaphoreSlim _probeLock = new(1, 1);

        // Unique suffix per worker instance so pipeline/element names never
        // collide when multiple cameras probe or stream concurrently.
        private readonly string _instanceId = Guid.NewGuid().ToString("N")[..8];

        private const int MaxRestarts = 10;

        // How long (ms) to wait after tearing down any pipeline before
        // re-opening the same MF device.  Media Foundation needs ~300–500 ms
        // to fully release an exclusive capture session on Windows.
        private const int MfReleaseSettleMs = 600;

        // Optional caps hint supplied by DeviceMonitorService when upgrading a
        // KS worker to MF — the KS probe already found working caps for this
        // physical camera, so we try them first before falling back to probing.
        private readonly string? _capsHint;
        private readonly bool _capsHintNeedsGrayConvert;

        public WebcamWorker(Gst.Device device, string host, int port, ILogger logger,
                            string? capsHint = null, bool capsHintNeedsGrayConvert = false)
        {
            _device = device;
            _deviceName = device.DisplayName ?? "Unknown";
            _host = host;
            _port = port;
            _logger = logger;
            _capsHint = capsHint;
            _capsHintNeedsGrayConvert = capsHintNeedsGrayConvert;
        }

        public string DeviceName => _deviceName;
        public string? CachedCaps => _cachedCaps;
        public bool CachedNeedsGrayConvert => _cachedNeedsGrayConvert;

        // ------------------------------------------------------------------ //
        //  Public API                                                          //
        // ------------------------------------------------------------------ //

        public async SysTask StartAsync(CancellationToken parentToken = default)
        {
            await _semaphore.WaitAsync(parentToken);
            try
            {
                if (_pipelineTask != null) return;
                _cts = CancellationTokenSource.CreateLinkedTokenSource(parentToken);
                _pipelineTask = SysTask.Run(() => RunPipelineAsync(_cts.Token));
            }
            finally { _semaphore.Release(); }
        }

        public async SysTask StopAsync()
        {
            await _semaphore.WaitAsync();
            try { _cts?.Cancel(); }
            finally { _semaphore.Release(); }

            // Eagerly tear down the GStreamer pipeline here rather than waiting
            // for the loop to exit on its own.  This immediately releases the
            // exclusive MF capture session so a replacement worker (e.g. KS→MF
            // upgrade) can open the device without hitting GST_FLOW_ERROR (-5).
            TearDownPipeline();

            if (_pipelineTask != null)
                await _pipelineTask;
        }

        public async ValueTask DisposeAsync()
        {
            await StopAsync();
            _cts?.Dispose();
            _semaphore.Dispose();
        }

        // ------------------------------------------------------------------ //
        //  Pipeline loop                                                       //
        // ------------------------------------------------------------------ //

        private async SysTask RunPipelineAsync(CancellationToken token)
        {
            int restartCount = 0;

            while (!token.IsCancellationRequested && restartCount < MaxRestarts)
            {
                _pipelineError = false;
                _deviceBusy = false;

                try
                {
                    // NOTE: Gst.Application.Init() must be called ONCE at process
                    // startup (DeviceMonitorService.ExecuteAsync), NOT here.
                    // Re-calling it corrupts shared GStreamer state and causes
                    // cross-pipeline interference across all running cameras.

                    await BuildPipelineAsync(token);

                    _logger.LogInformation("[{Device}] Setting pipeline to PLAYING", _deviceName);
                    var stateReturn = _pipeline!.SetState(State.Playing);
                    _logger.LogInformation("[{Device}] SetState(Playing) returned: {Return}",
                        _deviceName, stateReturn);

                    if (stateReturn == StateChangeReturn.Failure)
                    {
                        DrainBusErrors();
                        throw new Exception("SetState(Playing) returned Failure");
                    }

                    if (stateReturn == StateChangeReturn.Async)
                    {
                        _logger.LogInformation("[{Device}] Waiting for async state change...", _deviceName);
                        var waitReturn = _pipeline.GetState(
                            out State current, out State pending, 10 * Gst.Constants.SECOND);
                        _logger.LogInformation(
                            "[{Device}] GetState result: {Return} | current={Current} pending={Pending}",
                            _deviceName, waitReturn, current, pending);

                        if (waitReturn == StateChangeReturn.Failure)
                        {
                            DrainBusErrors();
                            throw new Exception("Async state change to Playing failed");
                        }
                    }

                    _logger.LogInformation("[{Device}] Pipeline is PLAYING - monitoring bus", _deviceName);

                    var bus = _pipeline.Bus!;
                    bus.AddSignalWatch();
                    bus.Message += OnBusMessage;

                    while (!token.IsCancellationRequested && !_pipelineError)
                        await SysTask.Delay(100, CancellationToken.None);

                    bus.Message -= OnBusMessage;
                    bus.RemoveSignalWatch();

                    if (token.IsCancellationRequested)
                        break;

                    throw new Exception("Pipeline error signalled from bus");
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    restartCount++;
                    _logger.LogWarning("[{Device}] Restart #{Count}: {Message}",
                        _deviceName, restartCount, ex.Message);

                    TearDownPipeline();

                    // GST_FLOW_ERROR (-5) means the MF capture session errored at
                    // the driver level.  Clear the caps cache so we re-probe on the
                    // next attempt (a different resolution may work after recovery).
                    if (_deviceBusy)
                    {
                        _logger.LogWarning(
                            "[{Device}] Device error (-5) — clearing caps cache, waiting for MF release",
                            _deviceName);
                        _cachedCaps = null;
                    }

                    // Exponential back-off capped at 5 s; add MF settle time when device errored.
                    int delayMs = Math.Min(500 * restartCount, 5000);
                    if (_deviceBusy) delayMs = Math.Max(delayMs, MfReleaseSettleMs * 2);

                    await SysTask.Delay(delayMs, CancellationToken.None);
                }
            }

            if (restartCount >= MaxRestarts)
                _logger.LogError("[{Device}] Too many failures — giving up", _deviceName);

            TearDownPipeline();

            await _semaphore.WaitAsync();
            try { _pipelineTask = null; }
            finally { _semaphore.Release(); }
        }

        // ------------------------------------------------------------------ //
        //  Pipeline construction                                               //
        // ------------------------------------------------------------------ //

        private async SysTask BuildPipelineAsync(CancellationToken token)
        {
            TearDownPipeline();

            // ----------------------------------------------------------------
            // Determine caps BEFORE creating the real pipeline so the test
            // pipeline is fully torn down and MF has released the device before
            // we open it again for real streaming.
            // ----------------------------------------------------------------
            string capsString;
            bool needsGrayConvert;

            if (_cachedCaps != null)
            {
                _logger.LogInformation("[{Device}] Using cached caps: {Caps}", _deviceName, _cachedCaps);
                capsString = _cachedCaps;
                needsGrayConvert = _cachedNeedsGrayConvert;
            }
            else if (_capsHint != null)
            {
                // A caps hint was supplied from a previous KS probe for this physical
                // camera.  Try it directly on the real pipeline — no probe needed.
                // If it fails at streaming time the restart loop will clear the cache
                // and fall through to full probing on the next attempt.
                _logger.LogInformation("[{Device}] Using KS caps hint: {Caps}", _deviceName, _capsHint);
                capsString = _capsHint;
                needsGrayConvert = _capsHintNeedsGrayConvert;
                _cachedCaps = capsString;
                _cachedNeedsGrayConvert = needsGrayConvert;
            }
            else
            {
                // Serialise probing: only one camera probes at a time to prevent
                // concurrent KS/MF device opens from causing mutual interference.
                await _probeLock.WaitAsync(token);
                try
                {
                    (capsString, needsGrayConvert) = ProbeDeviceCaps();
                }
                finally
                {
                    _probeLock.Release();
                }

                if (capsString == "video/x-raw")
                {
                    _logger.LogWarning(
                        "[{Device}] All caps probes failed — falling back to NV12 640x480",
                        _deviceName);
                    capsString = "video/x-raw,format=NV12,width=640,height=480,framerate=30/1";
                    needsGrayConvert = false;
                    // Don't cache — re-probe on next restart in case the device recovers
                }
                else
                {
                    _cachedCaps = capsString;
                    _cachedNeedsGrayConvert = needsGrayConvert;
                }
            }

            _logger.LogInformation("[{Device}] Negotiated caps: {Caps}", _deviceName, capsString);

            // Give MF time to fully release the capture session the probe held.
            _logger.LogDebug("[{Device}] Waiting {Ms}ms for MF device release after probe",
                _deviceName, MfReleaseSettleMs);
            await SysTask.Delay(MfReleaseSettleMs, token);

            // ----------------------------------------------------------------
            _pipeline = new Pipeline($"webcam-{_deviceName}-{_instanceId}");

            var source = _device.CreateElement("src")
                ?? throw new Exception($"[{_deviceName}] device.CreateElement() returned null");
            _logger.LogInformation("[{Device}] Source element created via device.CreateElement()",
                _deviceName);

            var capsFilter = ElementFactory.Make("capsfilter", "srcfilter")
                ?? throw new Exception("Failed to create source capsfilter");
            capsFilter["caps"] = Caps.FromString(capsString);

            Element? grayConvert = null;
            if (needsGrayConvert)
            {
                grayConvert = ElementFactory.Make("videoconvert", "grayconvert")
                    ?? throw new Exception("Failed to create grayconvert");
                _logger.LogInformation("[{Device}] Gray conversion stage added", _deviceName);
            }

            var convert = ElementFactory.Make("videoconvert", "convert")
                ?? throw new Exception("Failed to create videoconvert");

            var scale = ElementFactory.Make("videoscale", "scale")
                ?? throw new Exception("Failed to create videoscale");

            var scaleFilter = ElementFactory.Make("capsfilter", "scalefilter")
                ?? throw new Exception("Failed to create scale capsfilter");

            // Preserve native resolution for fixed-res devices (e.g. FLIR 640×512).
            bool isFixedRes = capsString.Contains(",height=512") ||
                              capsString.Contains(",height=256") ||
                              capsString.Contains(",height=288");
            string scaleCaps = isFixedRes
                ? "video/x-raw,format=I420"
                : "video/x-raw,format=I420,width=1280,height=720,framerate=30/1";
            scaleFilter["caps"] = Caps.FromString(scaleCaps);

            string? encoderFactory = FindAvailableEncoder();
            if (encoderFactory == null)
                throw new Exception(
                    "No H.264 encoder found. " +
                    "Windows: ensure GStreamer is fully installed. " +
                    "Linux: install gstreamer1-plugin-ugly (x264enc) or gstreamer1-plugin-openh264.");

            var encoder = ElementFactory.Make(encoderFactory, "encoder")
                ?? throw new Exception($"Failed to create encoder: {encoderFactory}");
            ConfigureEncoder(encoder, encoderFactory);
            _logger.LogInformation("[{Device}] Using encoder: {Encoder}", _deviceName, encoderFactory);

            var parse = ElementFactory.Make("h264parse", "parse")
                ?? throw new Exception("Failed to create h264parse");

            var rtp = ElementFactory.Make("rtph264pay", "rtp")
                ?? throw new Exception("Failed to create rtph264pay");
            rtp["config-interval"] = -1;
            rtp["pt"] = 96;
            rtp["mtu"] = (uint)1400;

            var sink = ElementFactory.Make("udpsink", "sink")
                ?? throw new Exception("Failed to create udpsink");
            sink["host"] = _host;
            sink["port"] = _port;
            sink["sync"] = false;

            if (needsGrayConvert && grayConvert != null)
            {
                _pipeline.Add(source, capsFilter, grayConvert, convert, scale,
                    scaleFilter, encoder, parse, rtp, sink);
                if (!Element.Link(source, capsFilter, grayConvert, convert, scale,
                        scaleFilter, encoder, parse, rtp, sink))
                    throw new Exception("Failed to link pipeline elements (gray path)");
            }
            else
            {
                _pipeline.Add(source, capsFilter, convert, scale, scaleFilter,
                    encoder, parse, rtp, sink);
                if (!Element.Link(source, capsFilter, convert, scale, scaleFilter,
                        encoder, parse, rtp, sink))
                    throw new Exception("Failed to link pipeline elements");
            }

            _logger.LogInformation("[{Device}] Pipeline built successfully", _deviceName);
        }

        // ------------------------------------------------------------------ //
        //  Caps probing                                                        //
        // ------------------------------------------------------------------ //

        private (string capsString, bool needsGrayConvert) ProbeDeviceCaps()
        {
            var advertisedCaps = GetAdvertisedCaps();
            _logger.LogInformation("[{Device}] Device advertises caps: {Caps}",
                _deviceName, advertisedCaps);

            var candidates = BuildCandidatesFromAdvertised(advertisedCaps);
            if (candidates.Count == 0)
            {
                _logger.LogDebug("[{Device}] Could not parse advertised caps — using fixed candidates",
                    _deviceName);
                candidates = GetFixedCandidates();
            }

            foreach (var (caps, isGray) in candidates)
            {
                _logger.LogDebug("[{Device}] Trying caps: {Caps}", _deviceName, caps);
                if (TestCapsWithPlaying(caps))
                {
                    _logger.LogInformation("[{Device}] Caps probe succeeded: {Caps}", _deviceName, caps);
                    return (caps, isGray);
                }
                _logger.LogDebug("[{Device}] Caps probe failed: {Caps}", _deviceName, caps);
            }

            _logger.LogWarning("[{Device}] All caps probes failed — will use ranked fallback caps",
                _deviceName);
            return ("video/x-raw", false);
        }

        private string GetAdvertisedCaps()
        {
            try
            {
                var probeSrc = _device.CreateElement($"caps-query-{_instanceId}");
                if (probeSrc == null) return "";

                probeSrc.SetState(State.Ready);
                probeSrc.GetState(out _, out _, Gst.Constants.SECOND);
                var pad = probeSrc.GetStaticPad("src");
                var caps = pad?.QueryCaps(null)?.ToString() ?? "";
                probeSrc.SetState(State.Null);
                probeSrc.Dispose();
                return caps;
            }
            catch { return ""; }
        }

        private List<(string caps, bool isGray)> BuildCandidatesFromAdvertised(string advertised)
        {
            var result = new List<(string, bool)>();
            if (string.IsNullOrEmpty(advertised)) return result;

            var formatPriority = new[] { "NV12", "I420", "YUY2", "UYVY", "NV21" };
            var resPriority = new[]
            {
                (1280, 720), (640, 480), (640, 512), (800, 600),
                (320, 256),  (640, 360), (352, 288), (320, 240)
            };
            var frameratePriority = new[] { "30/1", "15/1", "9/1", "15/2" };
            bool hasMjpeg = advertised.Contains("image/jpeg");

            // MF devices advertise capabilities as unbounded ranges, e.g.:
            //   width=(int)[ 1, 2147483647 ], height=(int)[ 1, 2147483647 ]
            // rather than exact values.  Detect this and treat all resolutions
            // as candidates — but cap the list to the 3 most common to avoid
            // holding the global probe lock for too long and starving other cameras.
            bool hasUnboundedRange = advertised.Contains("[ 1, 2147483647 ]");

            foreach (var (w, h) in resPriority)
            {
                bool supported = hasUnboundedRange || AdvertisedSupportsExact(advertised, w, h);
                if (!supported) continue;

                foreach (var fmt in formatPriority)
                {
                    if (!advertised.Contains(fmt)) continue;
                    foreach (var fps in frameratePriority)
                    {
                        result.Add(($"video/x-raw,format={fmt},width={w},height={h},framerate={fps}", false));
                        break; // highest supported fps only
                    }
                }

                foreach (var grayFmt in new[] { "GRAY16_LE", "GRAY8" })
                {
                    if (!advertised.Contains(grayFmt)) continue;
                    result.Add(($"video/x-raw,format={grayFmt},width={w},height={h},framerate=15/2", true));
                }

                // For MF unbounded-range devices stop after building 3 candidates.
                // Each probe takes up to 2 s and is serialised globally — testing
                // 40+ candidates would starve other cameras for minutes.
                if (hasUnboundedRange && result.Count >= 3)
                    break;
            }

            if (hasMjpeg)
                foreach (var (w, h) in resPriority)
                {
                    bool supported = hasUnboundedRange || AdvertisedSupportsExact(advertised, w, h);
                    if (supported)
                        result.Add(($"image/jpeg,width={w},height={h},framerate=30/1", false));
                }

            return result;
        }

        // Checks for exact discrete resolution in KS-style caps:
        //   width=(int)1280, height=(int)720
        private static bool AdvertisedSupportsExact(string advertised, int width, int height) =>
            advertised.Contains($"width=(int){width}") &&
            advertised.Contains($"height=(int){height}");

        private static List<(string caps, bool isGray)> GetFixedCandidates() =>
            new()
            {
                ("video/x-raw,format=NV12,width=1280,height=720,framerate=30/1", false),
                ("video/x-raw,format=YUY2,width=1280,height=720,framerate=30/1", false),
                ("video/x-raw,format=NV12,width=640,height=480,framerate=30/1",  false),
                ("video/x-raw,format=YUY2,width=640,height=480,framerate=30/1",  false),
                ("image/jpeg,width=1280,height=720,framerate=30/1",               false),
                ("image/jpeg,width=640,height=480,framerate=30/1",                false),
            };

        private bool TestCapsWithPlaying(string capsString)
        {
            // Each probe pipeline gets a globally unique name so concurrent probes
            // from different workers never collide in the GStreamer registry.
            string probeName = $"caps-probe-{_instanceId}-{Guid.NewGuid():N}";
            Pipeline? testPipeline = null;
            Element? testSrc = null;
            Element? testFilter = null;
            Element? testConvert = null;
            Element? testSink = null;

            // gotRealBuffer is set from the GStreamer streaming thread via a pad probe
            // when a buffer with valid (non-empty, has width+height) caps flows through.
            int gotRealBuffer = 0;

            try
            {
                testSrc = _device.CreateElement($"test-src-{probeName}");
                testFilter = ElementFactory.Make("capsfilter", "test-filter");
                testConvert = ElementFactory.Make("videoconvert", "test-convert");
                testSink = ElementFactory.Make("fakesink", "test-sink");

                if (testSrc == null || testFilter == null || testConvert == null || testSink == null)
                    return false;

                testFilter["caps"] = Caps.FromString(capsString);
                testSink["sync"] = false;

                testPipeline = new Pipeline(probeName);
                testPipeline.Add(testSrc, testFilter, testConvert, testSink);

                // Ownership transferred to pipeline — save names, null locals.
                var srcName = $"test-src-{probeName}";
                testSrc = testFilter = testConvert = testSink = null;

                if (!Element.Link(
                        testPipeline.GetByName(srcName),
                        testPipeline.GetByName("test-filter"),
                        testPipeline.GetByName("test-convert"),
                        testPipeline.GetByName("test-sink")))
                    return false;

                // Install a pad probe on the sink pad of fakesink.  The probe fires
                // on the GStreamer streaming thread (no GLib main loop required) when
                // a real buffer arrives.  We verify the negotiated caps have a valid
                // resolution to reject MF "empty stream" false positives.
                var fakeSinkEl = testPipeline.GetByName("test-sink");
                var fakeSinkPad = fakeSinkEl?.GetStaticPad("sink");
                ulong probeId = 0;
                if (fakeSinkPad != null)
                {
                    probeId = fakeSinkPad.AddProbe(PadProbeType.Buffer, (pad, info) =>
                    {
                        try
                        {
                            var currentCaps = pad.CurrentCaps ?? pad.QueryCaps(null);
                            if (currentCaps != null && !currentCaps.IsEmpty)
                            {
                                var s = currentCaps.GetStructure(0);
                                if (s != null)
                                {
                                    s.GetInt("width", out int w);
                                    s.GetInt("height", out int h);
                                    if (w > 0 && h > 0)
                                        Interlocked.Exchange(ref gotRealBuffer, 1);
                                }
                            }
                            // If caps unreadable, count the buffer anyway — a buffer
                            // arriving means the device is streaming.
                            if (gotRealBuffer == 0)
                                Interlocked.Exchange(ref gotRealBuffer, 1);
                        }
                        catch { /* ignore probe exceptions */ }

                        return PadProbeReturn.Ok;
                    });
                }

                var ret = testPipeline.SetState(State.Playing);
                if (ret == StateChangeReturn.Failure) return false;

                var bus = testPipeline.Bus;
                var deadline = System.DateTime.UtcNow.AddSeconds(2);

                while (System.DateTime.UtcNow < deadline)
                {
                    if (Volatile.Read(ref gotRealBuffer) == 1)
                        return true;

                    // Check bus for hard errors (fast path out)
                    var msg = bus.TimedPopFiltered(
                        100 * Gst.Constants.MSECOND,
                        MessageType.Error | MessageType.Eos);

                    if (msg == null) continue;

                    if (msg.Type == MessageType.Error)
                    {
                        _logger.LogDebug("[{Device}] Probe rejected {Caps}: {Msg}",
                            _deviceName, capsString, msg.Structure?.ToString() ?? "error");
                        return false;
                    }

                    // EOS without a real buffer = device opened but delivered nothing
                    return false;
                }

                // Timeout — no real buffer arrived, no error
                return false;
            }
            catch (Exception ex)
            {
                _logger.LogDebug("[{Device}] Probe exception for {Caps}: {Ex}",
                    _deviceName, capsString, ex.Message);
                return false;
            }
            finally
            {
                testPipeline?.SetState(State.Null);
                testPipeline?.Dispose();
                testSrc?.Dispose();
                testFilter?.Dispose();
                testConvert?.Dispose();
                testSink?.Dispose();
            }
        }

        // ------------------------------------------------------------------ //
        //  Encoder                                                             //
        // ------------------------------------------------------------------ //

        private static string? FindAvailableEncoder()
        {
            foreach (string factory in new[] { "x264enc", "openh264enc", "nvh264enc", "vtenc_h264" })
            {
                var test = ElementFactory.Make(factory, "test-enc");
                if (test != null) { test.Dispose(); return factory; }
            }
            return null;
        }

        private void ConfigureEncoder(Element enc, string factory)
        {
            switch (factory)
            {
                case "x264enc":
                    enc["speed-preset"] = (uint)5;
                    enc["tune"] = (uint)4;
                    enc["bitrate"] = (uint)3000;
                    enc["key-int-max"] = (uint)30;
                    enc["bframes"] = (uint)0;
                    break;
                case "openh264enc":
                    enc["bitrate"] = (uint)(3000 * 1000);
                    enc["gop-size"] = 30;
                    enc["complexity"] = 1;
                    enc["rate-control"] = 1;
                    break;
                case "nvh264enc":
                    enc["bitrate"] = (uint)3000;
                    enc["preset"] = 2;
                    enc["zerolatency"] = true;
                    enc["rc-mode"] = 1;
                    break;
                case "vtenc_h264":
                    enc["bitrate"] = (uint)3000;
                    enc["realtime"] = true;
                    enc["allow-frame-reordering"] = false;
                    break;
                default:
                    _logger.LogWarning("[{Device}] Unknown encoder {Enc} — using default properties",
                        _deviceName, factory);
                    break;
            }
        }

        // ------------------------------------------------------------------ //
        //  Bus helpers                                                         //
        // ------------------------------------------------------------------ //

        private void OnBusMessage(object? sender, MessageArgs args)
        {
            // This handler fires ONLY for messages on this pipeline's own bus.
            // GStreamer buses are per-pipeline — there is no cross-pipeline bleed.
            var msg = args.Message;
            switch (msg.Type)
            {
                case MessageType.Error:
                    {
                        var detail = msg.Structure?.ToString() ?? "";
                        _logger.LogError("[{Device}] GStreamer error: {Message}", _deviceName, detail);

                        // GST_FLOW_ERROR = -5: device-level driver error (not a caps issue).
                        // Signal the restart loop to use a longer back-off and re-probe caps.
                        if (detail.Contains("flow-return=(int)-5"))
                            _deviceBusy = true;

                        _pipelineError = true;
                        break;
                    }
                case MessageType.Warning:
                    _logger.LogWarning("[{Device}] GStreamer warning: {Message}",
                        _deviceName, msg.Structure?.ToString() ?? "no details");
                    break;
                case MessageType.Eos:
                    _logger.LogInformation("[{Device}] End of stream", _deviceName);
                    _pipelineError = true;
                    break;
                case MessageType.StateChanged:
                    if (msg.Src == _pipeline)
                    {
                        msg.ParseStateChanged(out State old, out State now, out State pending);
                        _logger.LogInformation(
                            "[{Device}] Pipeline state: {Old} \u21e6 {Now} (pending: {Pending})",
                            _deviceName, old, now, pending);
                    }
                    break;
            }
        }

        private void DrainBusErrors()
        {
            if (_pipeline == null) return;
            var bus = _pipeline.Bus;
            while (true)
            {
                var msg = bus.TimedPopFiltered(
                    2 * Gst.Constants.SECOND,
                    MessageType.Error | MessageType.Warning | MessageType.StateChanged);
                if (msg == null) break;

                switch (msg.Type)
                {
                    case MessageType.Error:
                        {
                            var detail = msg.Structure?.ToString() ?? "no details";
                            _logger.LogError("[{Device}] Drain error: {Message}", _deviceName, detail);
                            if (detail.Contains("flow-return=(int)-5"))
                                _deviceBusy = true;
                            break;
                        }
                    case MessageType.Warning:
                        _logger.LogWarning("[{Device}] Drain warning: {Message}",
                            _deviceName, msg.Structure?.ToString() ?? "no details");
                        break;
                    case MessageType.StateChanged:
                        msg.ParseStateChanged(out State old, out State now, out State pending);
                        _logger.LogDebug(
                            "[{Device}] Drain state: {Old} \u21e6 {Now} (pending: {Pending})",
                            _deviceName, old, now, pending);
                        break;
                }
            }
        }

        // ------------------------------------------------------------------ //
        //  Teardown                                                            //
        // ------------------------------------------------------------------ //

        private void TearDownPipeline()
        {
            // Atomically take ownership of _pipeline so concurrent calls from
            // StopAsync and the pipeline loop are both safe — only one gets a
            // non-null reference and does the actual teardown.
            var p = Interlocked.Exchange(ref _pipeline, null);
            if (p == null) return;
            p.SetState(State.Null);
            p.Dispose();
        }
    }
}