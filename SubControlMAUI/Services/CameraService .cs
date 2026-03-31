//using System;
//using System.Collections.Concurrent;
//using System.Runtime.InteropServices;
//using System.Threading;
//using System.Threading.Tasks;
//using Gst;
//using Microsoft.Extensions.Logging;
//using SysBuffer = System.Buffer;
//using SysTask = System.Threading.Tasks.Task;

//namespace SubConsole.Services;

///// <summary>
///// Receives the RTP/H.264 UDP stream produced by <see cref="WebcamWorker"/> and
///// decodes it into raw BGRA frames, raising <see cref="FrameReady"/> on each one.
/////
///// Threading model
///// ───────────────
///// • Each registered worker gets one <see cref="ReceivePipeline"/> that owns a
/////   GStreamer udpsrc → decode → appsink chain running on a background task.
///// • Decoded frames are double-buffered: the GStreamer streaming thread writes
/////   into the back buffer and atomically swaps it to front; <see cref="FrameReady"/>
/////   subscribers always see a complete frame without blocking the decode thread.
///// • All public methods are safe to call from any thread.
///// • <see cref="StartAsync"/> / <see cref="StopAsync"/> are idempotent.
///// </summary>
//public sealed class CameraService : IAsyncDisposable
//{
//    // ------------------------------------------------------------------ //
//    //  Public event                                                        //
//    // ------------------------------------------------------------------ //

//    /// <summary>Raised on the GStreamer streaming thread for every decoded BGRA frame.</summary>
//    public event Action<byte[], int, int>? FrameReady;

//    // ------------------------------------------------------------------ //
//    //  State                                                               //
//    // ------------------------------------------------------------------ //

//    private readonly ILogger<CameraService> _logger;

//    // Active receive pipelines keyed by the UDP port they listen on.
//    private readonly ConcurrentDictionary<int, ReceivePipeline> _receivers = new();

//    // Guards StartAsync / StopAsync so concurrent calls are serialised.
//    private readonly SemaphoreSlim _stateLock = new(1, 1);

//    private CancellationTokenSource? _cts;
//    private volatile bool _started;

//    // Double-buffer for lock-free frame hand-off between the GStreamer
//    // streaming thread (writer) and FrameReady subscribers (readers).
//    private byte[]? _frontBuffer;
//    private byte[]? _backBuffer;

//    // ------------------------------------------------------------------ //
//    //  Construction                                                        //
//    // ------------------------------------------------------------------ //

//    public CameraService(ILogger<CameraService> logger)
//    {
//        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
//    }

//    // ------------------------------------------------------------------ //
//    //  Lifecycle                                                           //
//    // ------------------------------------------------------------------ //

//    public async SysTask StartAsync(CancellationToken cancellationToken = default)
//    {
//        await _stateLock.WaitAsync(cancellationToken);
//        try
//        {
//            if (_started) return;
//            _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
//            _started = true;
//            _logger.LogInformation("[CameraService] Started");
//        }
//        finally { _stateLock.Release(); }
//    }

//    public async SysTask StopAsync()
//    {
//        await _stateLock.WaitAsync();
//        try
//        {
//            if (!_started) return;
//            _started = false;
//            _cts?.Cancel();
//        }
//        finally { _stateLock.Release(); }

//        await TearDownAllReceiversAsync();
//        _logger.LogInformation("[CameraService] Stopped");
//    }

//    // ------------------------------------------------------------------ //
//    //  Worker registration                                                 //
//    // ------------------------------------------------------------------ //

//    /// <summary>
//    /// Opens a receive pipeline for <paramref name="worker"/> on
//    /// <paramref name="udpPort"/>.  Must be called after <see cref="StartAsync"/>.
//    /// </summary>
//    public void RegisterWorker(WebcamWorker worker, int udpPort)
//    {
//        ArgumentNullException.ThrowIfNull(worker);

//        if (!_started)
//            throw new InvalidOperationException(
//                "CameraService must be started before registering workers.");

//        _receivers.GetOrAdd(udpPort, port =>
//        {
//            _logger.LogInformation(
//                "[CameraService] Opening receive pipeline on UDP:{Port} for [{Device}]",
//                port, worker.DeviceName);

//            var rp = new ReceivePipeline(worker.DeviceName, port, OnFrameDecoded, _logger);
//            rp.Start(_cts!.Token);
//            return rp;
//        });
//    }

//    /// <summary>
//    /// Tears down the receive pipeline for <paramref name="deviceName"/>.
//    /// </summary>
//    public async SysTask UnregisterWorkerAsync(string deviceName)
//    {
//        foreach (var (port, rp) in _receivers)
//        {
//            if (rp.DeviceName != deviceName) continue;
//            if (!_receivers.TryRemove(port, out var removed)) continue;

//            _logger.LogInformation(
//                "[CameraService] Closing receive pipeline on UDP:{Port} for [{Device}]",
//                port, deviceName);

//            await removed.DisposeAsync();
//        }
//    }

//    // ------------------------------------------------------------------ //
//    //  IAsyncDisposable                                                    //
//    // ------------------------------------------------------------------ //

//    public async ValueTask DisposeAsync()
//    {
//        await StopAsync();
//        _cts?.Dispose();
//        _stateLock.Dispose();
//    }

//    // ------------------------------------------------------------------ //
//    //  Frame hand-off                                                      //
//    // ------------------------------------------------------------------ //

//    private void OnFrameDecoded(byte[] data, int width, int height)
//    {
//        int needed = data.Length;

//        // Grow buffers only when resolution changes.
//        if (_backBuffer == null || _backBuffer.Length != needed)
//        {
//            _backBuffer = new byte[needed];
//            _frontBuffer = new byte[needed];
//        }

//        SysBuffer.BlockCopy(data, 0, _backBuffer!, 0, needed);

//        // Atomically promote back → front so readers never see a partial frame.
//        (_frontBuffer, _backBuffer) = (_backBuffer, _frontBuffer);

//        try
//        {
//            FrameReady?.Invoke(_frontBuffer!, width, height);
//        }
//        catch (Exception ex)
//        {
//            _logger.LogWarning(ex, "[CameraService] FrameReady subscriber threw");
//        }
//    }

//    // ------------------------------------------------------------------ //
//    //  Helpers                                                             //
//    // ------------------------------------------------------------------ //

//    private async SysTask TearDownAllReceiversAsync()
//    {
//        var teardowns = new SysTask[_receivers.Count];
//        int i = 0;
//        foreach (var (port, rp) in _receivers)
//        {
//            teardowns[i++] = rp.DisposeAsync().AsTask();
//            _receivers.TryRemove(port, out _);
//        }
//        await SysTask.WhenAll(teardowns);
//    }

//    // ------------------------------------------------------------------ //
//    //  ReceivePipeline                                                     //
//    // ------------------------------------------------------------------ //

//    /// <summary>
//    /// Owns a single GStreamer receive chain for one UDP port.
//    /// Pipeline: udpsrc → rtpjitterbuffer → rtph264depay → h264parse
//    ///                   → avdec_h264 → videoconvert → appsink (BGRA)
//    /// </summary>
//    private sealed class ReceivePipeline : IAsyncDisposable
//    {
//        public string DeviceName { get; }

//        private readonly int _port;
//        private readonly Action<byte[], int, int> _onFrame;
//        private readonly ILogger _logger;

//        // Unique suffix so concurrent pipelines never share element names in the
//        // GStreamer registry — mirrors the _instanceId pattern in WebcamWorker.
//        private readonly string _id = Guid.NewGuid().ToString("N")[..8];

//        private Pipeline? _pipeline;
//        private CancellationTokenSource? _cts;
//        private SysTask? _loopTask;

//        private const int MaxRestarts = 5;

//        public ReceivePipeline(
//            string deviceName, int port,
//            Action<byte[], int, int> onFrame,
//            ILogger logger)
//        {
//            DeviceName = deviceName;
//            _port = port;
//            _onFrame = onFrame;
//            _logger = logger;
//        }

//        public void Start(CancellationToken parentToken)
//        {
//            _cts = CancellationTokenSource.CreateLinkedTokenSource(parentToken);
//            _loopTask = SysTask.Run(() => RunAsync(_cts.Token));
//        }

//        // ----------------------------------------------------------------
//        //  Pipeline loop
//        // ----------------------------------------------------------------

//        private async SysTask RunAsync(CancellationToken token)
//        {
//            int restarts = 0;

//            while (!token.IsCancellationRequested && restarts < MaxRestarts)
//            {
//                bool pipelineError = false;

//                try
//                {
//                    BuildPipeline();

//                    var stateReturn = _pipeline!.SetState(State.Playing);

//                    if (stateReturn == StateChangeReturn.Failure)
//                        throw new Exception($"[UDP:{_port}] SetState(Playing) returned Failure");

//                    if (stateReturn == StateChangeReturn.Async)
//                    {
//                        var waitReturn = _pipeline.GetState(
//                            out _, out _, 5 * Gst.Constants.SECOND);

//                        if (waitReturn == StateChangeReturn.Failure)
//                            throw new Exception($"[UDP:{_port}] Async state change to Playing failed");
//                    }

//                    _logger.LogInformation(
//                        "[CameraService] Receive pipeline PLAYING on UDP:{Port} for [{Device}]",
//                        _port, DeviceName);

//                    restarts = 0; // reset on successful start

//                    var bus = _pipeline.Bus!;
//                    bus.AddSignalWatch();
//                    bus.Message += (_, args) =>
//                    {
//                        var msg = args.Message;
//                        if (msg.Type == MessageType.Error || msg.Type == MessageType.Eos)
//                        {
//                            _logger.LogWarning(
//                                "[CameraService] Bus {Type} on UDP:{Port}: {Detail}",
//                                msg.Type, _port, msg.Structure?.ToString() ?? "–");
//                            pipelineError = true;
//                        }
//                    };

//                    while (!token.IsCancellationRequested && !pipelineError)
//                        await SysTask.Delay(100, CancellationToken.None);

//                    bus.RemoveSignalWatch();

//                    if (token.IsCancellationRequested) break;

//                    throw new Exception($"[UDP:{_port}] Pipeline error signalled from bus");
//                }
//                catch (OperationCanceledException) { break; }
//                catch (Exception ex)
//                {
//                    restarts++;
//                    _logger.LogWarning(
//                        "[CameraService] Receive pipeline restart #{Count}/{Max} on UDP:{Port}: {Message}",
//                        restarts, MaxRestarts, _port, ex.Message);

//                    TearDown();
//                    await SysTask.Delay(
//                        Math.Min(500 * restarts, 5000), CancellationToken.None);
//                }
//            }

//            if (restarts >= MaxRestarts)
//                _logger.LogError(
//                    "[CameraService] Too many failures on UDP:{Port} for [{Device}] — giving up",
//                    _port, DeviceName);

//            TearDown();
//        }

//        // ----------------------------------------------------------------
//        //  Pipeline construction
//        // ----------------------------------------------------------------

//        private void BuildPipeline()
//        {
//            TearDown();

//            _pipeline = new Pipeline($"recv-{_id}");

//            var udpSrc = ElementFactory.Make("udpsrc", "udpsrc")
//                ?? throw new Exception($"[UDP:{_port}] Cannot create udpsrc");
//            udpSrc["port"] = _port;
//            // Caps must match what WebcamWorker's rtph264pay publishes (pt=96).
//            udpSrc["caps"] = Caps.FromString(
//                "application/x-rtp,media=video,payload=96,encoding-name=H264,clock-rate=90000");

//            var jitter = ElementFactory.Make("rtpjitterbuffer", "jitter")
//                ?? throw new Exception($"[UDP:{_port}] Cannot create rtpjitterbuffer");
//            jitter["latency"] = (uint)100; // 100 ms jitter budget — tune per network
//            jitter["do-lost"] = true;

//            var depay = ElementFactory.Make("rtph264depay", "depay")
//                ?? throw new Exception($"[UDP:{_port}] Cannot create rtph264depay");

//            var parse = ElementFactory.Make("h264parse", "parse")
//                ?? throw new Exception($"[UDP:{_port}] Cannot create h264parse");

//            var decode = ElementFactory.Make("avdec_h264", "decode")
//                ?? throw new Exception(
//                    $"[UDP:{_port}] Cannot create avdec_h264. " +
//                    "Install gstreamer1-libav (Linux) or the GStreamer libav plugin (Windows).");

//            var convert = ElementFactory.Make("videoconvert", "convert")
//                ?? throw new Exception($"[UDP:{_port}] Cannot create videoconvert");

//            var sink = ElementFactory.Make("appsink", "sink")
//                ?? throw new Exception($"[UDP:{_port}] Cannot create appsink");

//            // BGRA output — directly usable as a GPU texture with no further conversion.
//            sink["caps"] = Caps.FromString("video/x-raw,format=BGRA");
//            sink["sync"] = false;
//            sink["emit-signals"] = true;
//            sink["max-buffers"] = (uint)2; // drop stale frames; never back-pressure decoder
//            sink["drop"] = true;

//            _pipeline.Add(udpSrc, jitter, depay, parse, decode, convert, sink);

//            if (!Element.Link(udpSrc, jitter, depay, parse, decode, convert, sink))
//                throw new Exception($"[UDP:{_port}] Failed to link receive pipeline elements");

//            // NewSample fires on the GStreamer streaming thread — keep the handler fast.
//            ((Gst.App.AppSink)_pipeline.GetByName("sink")).NewSample += OnNewSample;
//        }

//        // ----------------------------------------------------------------
//        //  Frame extraction
//        // ----------------------------------------------------------------

//        private void OnNewSample(object? sender, GLib.SignalArgs _)
//        {
//            if (sender is not Gst.App.AppSink appSink) return;

//            using var sample = appSink.TryPullSample(0);
//            if (sample == null) return;

//            using var caps = sample.Caps;
//            using var buffer = sample.Buffer;
//            if (caps == null || buffer == null) return;

//            var structure = caps.GetStructure(0);
//            if (structure == null) return;

//            structure.GetInt("width", out int width);
//            structure.GetInt("height", out int height);
//            if (width <= 0 || height <= 0) return;

//            if (!buffer.Map(out MapInfo map, MapFlags.Read)) return;
//            try
//            {
//                var data = new byte[(int)map.Size];
//                Marshal.Copy(map.DataPtr, data, 0, data.Length);
//                _onFrame(data, width, height);
//            }
//            finally
//            {
//                buffer.Unmap(map);
//            }
//        }

//        // ----------------------------------------------------------------
//        //  Teardown
//        // ----------------------------------------------------------------

//        private void TearDown()
//        {
//            var p = Interlocked.Exchange(ref _pipeline, null);
//            if (p == null) return;

//            // Unhook the appsink signal before transitioning to Null so no
//            // callback fires during teardown — mirrors WebcamWorker's bus cleanup.
//            try
//            {
//                if (p.GetByName("sink") is Gst.App.AppSink s)
//                    s.NewSample -= OnNewSample;
//            }
//            catch { /* best-effort */ }

//            p.SetState(State.Null);
//            p.Dispose();
//        }

//        public async ValueTask DisposeAsync()
//        {
//            _cts?.Cancel();
//            if (_loopTask != null)
//                await _loopTask;
//            _cts?.Dispose();
//        }
//    }
//}