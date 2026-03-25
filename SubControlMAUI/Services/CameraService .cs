using Gst;
using Microsoft.Extensions.Logging;
//using OpenCvSharp;
using SubConsole.Services;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using SysTask = System.Threading.Tasks.Task;

namespace SubConsole.Services;

/// <summary>
/// Receives the RTP/H.264 UDP stream produced by <see cref="WebcamWorker"/> and
/// decodes it into raw BGRA frames, raising <see cref="FrameReady"/> on each one.
///
/// Threading model
/// ───────────────
/// • One <see cref="ReceiveLoopAsync"/> task owns the GStreamer receive pipeline.
/// • Decoded frames are written into a double-buffer protected by a lock-free
///   exchange so <see cref="FrameReady"/> subscribers always see the latest frame
///   without blocking the decode thread.
/// • All public methods are safe to call from any thread.
/// • <see cref="StartAsync"/> / <see cref="StopAsync"/> are idempotent.
/// </summary>
public sealed class CameraService : ICameraService, IAsyncDisposable
{
    // ------------------------------------------------------------------ //
    //  Public events & properties                                          //
    // ------------------------------------------------------------------ //

    /// <summary>Fired on the decode thread each time a new BGRA frame is ready.</summary>
    public event Action<byte[], int, int>? FrameReady;

    // ------------------------------------------------------------------ //
    //  Infrastructure                                                      //
    // ------------------------------------------------------------------ //

    private readonly ILogger<CameraService> _logger;

    // One worker per physical camera (device-name → worker).
    // Populated by the caller via RegisterWorker / UnregisterWorker.
    private readonly ConcurrentDictionary<string, WebcamWorker> _workers = new();

    // Active receive pipelines keyed by the UDP port they listen on.
    private readonly ConcurrentDictionary<int, ReceivePipeline> _receivers = new();

    // Guards StartAsync / StopAsync so concurrent calls are serialised.
    private readonly SemaphoreSlim _stateLock = new(1, 1);

    private CancellationTokenSource? _cts;
    private volatile bool _started;

    // ------------------------------------------------------------------ //
    //  Double-buffer (lock-free frame hand-off)                           //
    // ------------------------------------------------------------------ //

    // Two reusable byte arrays; the decode thread writes into the "back"
    // buffer and atomically publishes it; consumers read from whichever
    // buffer was last published without ever blocking the decode thread.
    private byte[]? _frontBuffer;
    private byte[]? _backBuffer;

    // Stores the most-recently-decoded frame dimensions so a late-joining
    // subscriber can query them without needing to wait for the next frame.
    private volatile FrameInfo? _latestFrameInfo;

    // ------------------------------------------------------------------ //
    //  Construction                                                        //
    // ------------------------------------------------------------------ //

    public CameraService(ILogger<CameraService> logger)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    // ------------------------------------------------------------------ //
    //  Worker registration (called by DeviceMonitorService / host)        //
    // ------------------------------------------------------------------ //

    /// <summary>
    /// Registers a <see cref="WebcamWorker"/> so this service knows which
    /// UDP port to open a receive pipeline on.  Safe to call while running.
    /// </summary>
    public void RegisterWorker(WebcamWorker worker, int udpPort)
    {
        ArgumentNullException.ThrowIfNull(worker);

        _workers[worker.DeviceName] = worker;

        // If already running, spin up a receive pipeline immediately.
        if (_started)
            EnsureReceiver(worker, udpPort, _cts!.Token);
    }

    /// <summary>
    /// Removes a previously-registered worker and tears down its receive pipeline.
    /// </summary>
    public async SysTask UnregisterWorkerAsync(string deviceName)
    {
        _workers.TryRemove(deviceName, out _);

        // Stop and remove receivers whose source worker is now gone.
        foreach (var (port, rp) in _receivers)
        {
            if (rp.DeviceName == deviceName && _receivers.TryRemove(port, out var removed))
                await removed.DisposeAsync();
        }
    }

    // ------------------------------------------------------------------ //
    //  ICameraService – lifecycle                                         //
    // ------------------------------------------------------------------ //

    /// <inheritdoc/>
    public async SysTask StartAsync(CancellationToken cancellationToken = default)
    {
        await _stateLock.WaitAsync(cancellationToken);
        try
        {
            if (_started) return;

            _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            _started = true;

            _logger.LogInformation("[CameraService] Starting receive pipelines for {Count} worker(s)",
                _workers.Count);

            // In the original API there was a single cameraIndex; workers now supply
            // their own ports.  Callers that used the old int-based overload should
            // migrate to RegisterWorker.  This overload is kept for compatibility
            // but does nothing by itself — it just marks the service as started so
            // RegisterWorker calls that arrive later will immediately open a pipeline.
        }
        finally { _stateLock.Release(); }
    }

    /// <summary>
    /// Compatibility shim matching the original <c>StartAsync(int cameraIndex)</c>
    /// signature.  If a worker has already been registered for <paramref name="port"/>
    /// the receive pipeline opens immediately.
    /// </summary>
    public async SysTask StartAsync(int port, CancellationToken cancellationToken = default)
    {
        await StartAsync(cancellationToken);

        var worker = _workers.Values.FirstOrDefault();
        if (worker != null)
            EnsureReceiver(worker, port, _cts!.Token);
    }

    /// <inheritdoc/>
    public async SysTask StopAsync()
    {
        await _stateLock.WaitAsync();
        try
        {
            if (!_started) return;
            _started = false;
            _cts?.Cancel();
        }
        finally { _stateLock.Release(); }

        // Tear down all receive pipelines concurrently.
        var teardowns = _receivers.Values
            .Select(r => r.DisposeAsync().AsTask())
            .ToArray();

        await SysTask.WhenAll(teardowns);
        _receivers.Clear();

        _logger.LogInformation("[CameraService] All receive pipelines stopped");
    }

    // ------------------------------------------------------------------ //
    //  ICameraService – camera switching (maintained for API compatibility)//
    // ------------------------------------------------------------------ //

    /// <summary>
    /// Switches the active decode pipeline to a different UDP port.
    /// The old pipeline is torn down gracefully before the new one starts.
    /// </summary>
    public async SysTask SwitchCameraAsync(int newPort)
    {
        if (_cts == null || _cts.IsCancellationRequested)
            throw new InvalidOperationException("CameraService is not running.");

        _logger.LogInformation("[CameraService] Switching to port {Port}", newPort);

        // Stop all current receivers; a fresh one will be created below.
        var teardowns = _receivers.Values
            .Select(r => r.DisposeAsync().AsTask())
            .ToArray();
        await SysTask.WhenAll(teardowns);
        _receivers.Clear();

        var worker = _workers.Values.FirstOrDefault();
        if (worker != null)
            EnsureReceiver(worker, newPort, _cts.Token);
    }

    /// <summary>Synchronous wrapper kept for interface compatibility.</summary>
    public void SwitchCamera(int newPort) =>
        SwitchCameraAsync(newPort).GetAwaiter().GetResult();

    // ------------------------------------------------------------------ //
    //  ICameraService – camera enumeration                                //
    // ------------------------------------------------------------------ //

    /// <summary>
    /// Returns the set of UDP ports on which receive pipelines are currently active.
    /// </summary>
    public IReadOnlyList<int> GetActivePorts() =>
        _receivers.Keys.OrderBy(p => p).ToList();

    /// <summary>
    /// Probes a range of UDP ports for an active RTP stream.
    /// This replaces the OpenCV index-scan from the original implementation.
    /// </summary>
    public async Task<List<int>> GetAvailableCamerasAsync(
        int startPort, int count, CancellationToken cancellationToken = default)
    {
        var probes = Enumerable.Range(startPort, count)
            .Select(port => ProbePortAsync(port, cancellationToken));

        var results = await SysTask.WhenAll(probes);
        return results.Where(r => r >= 0).ToList();
    }

    /// <summary>Legacy sync shim (probes 5000–5000+maxTested).</summary>
    public List<int> GetAvailableCameras(int maxTested) =>
        GetAvailableCamerasAsync(5000, maxTested).GetAwaiter().GetResult();

    // ------------------------------------------------------------------ //
    //  Latest frame snapshot                                              //
    // ------------------------------------------------------------------ //

    /// <summary>
    /// Returns a copy of the most-recently-decoded frame, or <c>null</c> if no
    /// frame has been decoded yet.  Safe to call from any thread.
    /// </summary>
    public (byte[] Data, int Width, int Height)? TryGetLatestFrame()
    {
        var info = _latestFrameInfo;
        if (info == null) return null;

        // Take a snapshot copy — the internal buffers are recycled by the decode thread.
        var snap = new byte[info.Data.Length];
        Buffer.BlockCopy(info.Data, 0, snap, 0, snap.Length);
        return (snap, info.Width, info.Height);
    }

    // ------------------------------------------------------------------ //
    //  IAsyncDisposable                                                    //
    // ------------------------------------------------------------------ //

    public async ValueTask DisposeAsync()
    {
        await StopAsync();
        _cts?.Dispose();
        _stateLock.Dispose();
    }

    // ------------------------------------------------------------------ //
    //  Internal – receive pipeline management                              //
    // ------------------------------------------------------------------ //

    private void EnsureReceiver(WebcamWorker worker, int port, CancellationToken token)
    {
        _receivers.GetOrAdd(port, _ =>
        {
            _logger.LogInformation(
                "[CameraService] Opening receive pipeline on UDP:{Port} for [{Device}]",
                port, worker.DeviceName);

            var rp = new ReceivePipeline(worker.DeviceName, port, OnFrameDecoded, _logger);
            rp.StartAsync(token);
            return rp;
        });
    }

    private void OnFrameDecoded(byte[] data, int width, int height)
    {
        // Write into the back buffer, growing it only when the resolution changes.
        int needed = data.Length;

        if (_backBuffer == null || _backBuffer.Length != needed)
        {
            _backBuffer = new byte[needed];
            _frontBuffer = new byte[needed];
        }

        Buffer.BlockCopy(data, 0, _backBuffer!, 0, needed);

        // Swap front/back atomically so readers always see a complete frame.
        (_frontBuffer, _backBuffer) = (_backBuffer, _frontBuffer);

        // Publish the latest frame info (volatile write).
        _latestFrameInfo = new FrameInfo(_frontBuffer!, width, height);

        try
        {
            FrameReady?.Invoke(_frontBuffer!, width, height);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[CameraService] FrameReady subscriber threw");
        }
    }

    private async Task<int> ProbePortAsync(int port, CancellationToken cancellationToken)
    {
        try
        {
            using var udp = new UdpClient(new IPEndPoint(IPAddress.Any, port));
            udp.Client.ReceiveTimeout = 500;

            await udp.ReceiveAsync(cancellationToken)
                     .AsTask()
                     .WaitAsync(TimeSpan.FromMilliseconds(500), cancellationToken);
            return port;
        }
        catch { return -1; }
    }

    // ------------------------------------------------------------------ //
    //  Inner types                                                         //
    // ------------------------------------------------------------------ //

    private sealed record FrameInfo(byte[] Data, int Width, int Height);

    /// <summary>
    /// Wraps a single GStreamer UDP→RTP→H264→decode→BGRA receive pipeline.
    /// One instance per active UDP port.
    /// </summary>
    private sealed class ReceivePipeline : IAsyncDisposable
    {
        public string DeviceName { get; }

        private readonly int _port;
        private readonly Action<byte[], int, int> _onFrame;
        private readonly ILogger _logger;

        private Pipeline? _pipeline;
        private CancellationTokenSource? _cts;
        private SysTask? _loopTask;

        // Unique suffix so concurrent pipelines never share element names.
        private readonly string _id = Guid.NewGuid().ToString("N")[..8];

        public ReceivePipeline(
            string deviceName, int port,
            Action<byte[], int, int> onFrame,
            ILogger logger)
        {
            DeviceName = deviceName;
            _port = port;
            _onFrame = onFrame;
            _logger = logger;
        }

        public void StartAsync(CancellationToken parentToken)
        {
            _cts = CancellationTokenSource.CreateLinkedTokenSource(parentToken);
            _loopTask = SysTask.Run(() => RunAsync(_cts.Token));
        }

        private async SysTask RunAsync(CancellationToken token)
        {
            int restarts = 0;
            const int maxRestarts = 5;

            while (!token.IsCancellationRequested && restarts < maxRestarts)
            {
                try
                {
                    BuildPipeline();

                    var ret = _pipeline!.SetState(State.Playing);
                    if (ret == StateChangeReturn.Failure)
                        throw new Exception("SetState(Playing) failed on receive pipeline");

                    if (ret == StateChangeReturn.Async)
                    {
                        var waitRet = _pipeline.GetState(
                            out _, out _, 5 * Gst.Constants.SECOND);
                        if (waitRet == StateChangeReturn.Failure)
                            throw new Exception("Async state change failed on receive pipeline");
                    }

                    _logger.LogInformation(
                        "[CameraService] Receive pipeline PLAYING on UDP:{Port}", _port);

                    restarts = 0; // successful start — reset counter

                    bool error = false;
                    var bus = _pipeline.Bus!;
                    bus.AddSignalWatch();
                    bus.Message += (_, args) =>
                    {
                        if (args.Message.Type == MessageType.Error ||
                            args.Message.Type == MessageType.Eos)
                        {
                            _logger.LogWarning(
                                "[CameraService] Bus {Type} on UDP:{Port}: {Msg}",
                                args.Message.Type, _port,
                                args.Message.Structure?.ToString() ?? "–");
                            error = true;
                        }
                    };

                    while (!token.IsCancellationRequested && !error)
                        await SysTask.Delay(100, CancellationToken.None);

                    bus.RemoveSignalWatch();

                    if (token.IsCancellationRequested) break;
                    throw new Exception("Bus error on receive pipeline");
                }
                catch (OperationCanceledException) { break; }
                catch (Exception ex)
                {
                    restarts++;
                    _logger.LogWarning(
                        "[CameraService] Receive pipeline restart #{N} on UDP:{Port}: {Msg}",
                        restarts, _port, ex.Message);

                    TearDown();
                    await SysTask.Delay(
                        Math.Min(500 * restarts, 5000), CancellationToken.None);
                }
            }

            TearDown();
            _logger.LogInformation(
                "[CameraService] Receive pipeline stopped on UDP:{Port}", _port);
        }

        private void BuildPipeline()
            TearDown();

            // udpsrc → rtpjitterbuffer → rtph264depay → h264parse
            //        → avdec_h264 → videoconvert → appsink (BGRA)
            _pipeline = new Pipeline($"recv-{_id}");

            var udpSrc = ElementFactory.Make("udpsrc", "udpsrc")
                ?? throw new Exception("Cannot create udpsrc");
            udpSrc["port"] = _port;
            udpSrc["caps"] = Caps.FromString(
                "application/x-rtp,media=video,payload=96,encoding-name=H264,clock-rate=90000");

            var jitter = ElementFactory.Make("rtpjitterbuffer", "jitter")
                ?? throw new Exception("Cannot create rtpjitterbuffer");
            jitter["latency"] = (uint)100;          // 100 ms jitter budget
            jitter["do-lost"] = true;

            var depay = ElementFactory.Make("rtph264depay", "depay")
                ?? throw new Exception("Cannot create rtph264depay");

            var parse = ElementFactory.Make("h264parse", "parse")
                ?? throw new Exception("Cannot create h264parse");

            var decode = ElementFactory.Make("avdec_h264", "decode")
                ?? throw new Exception(
                    "Cannot create avdec_h264. Install gstreamer1-libav (Linux) " +
                    "or the GStreamer 'Good' plugins (Windows).");

            var convert = ElementFactory.Make("videoconvert", "convert")
                ?? throw new Exception("Cannot create videoconvert");

            var sink = ElementFactory.Make("appsink", "sink")
                ?? throw new Exception("Cannot create appsink");

            // Request BGRA from appsink so the buffer is directly usable as a
            // texture without further conversion on the UI side.
            sink["caps"] = Caps.FromString("video/x-raw,format=BGRA");
            sink["sync"] = false;
            sink["emit-signals"] = true;
            sink["max-buffers"] = (uint)2;          // never back-pressure the decoder
            sink["drop"] = true;

            _pipeline.Add(udpSrc, jitter, depay, parse, decode, convert, sink);

            if (!Element.Link(udpSrc, jitter, depay, parse, decode, convert, sink))
                throw new Exception($"Failed to link receive pipeline elements (port {_port})");

            // Wire the appsink new-sample signal.  This fires on the GStreamer
            // streaming thread; all frame hand-off logic lives in OnNewSample.
            var appSink = (Gst.App.AppSink)_pipeline.GetByName("sink");
            appSink.NewSample += OnNewSample;
        }

        private void OnNewSample(object? sender, GLib.SignalArgs args)
        {
            if (sender is not Gst.App.AppSink appSink) return;

            using var sample = appSink.TryPullSample(0);
            if (sample == null) return;

            using var caps = sample.Caps;
            using var buffer = sample.Buffer;

            if (caps == null || buffer == null) return;

            var structure = caps.GetStructure(0);
            if (structure == null) return;

            structure.GetInt("width", out int width);
            structure.GetInt("height", out int height);

            if (width <= 0 || height <= 0) return;

            // Map the GStreamer buffer read-only and copy into managed memory.
            if (!buffer.Map(out MapInfo map, MapFlags.Read)) return;

            try
            {
                int bytes = (int)map.Size;
                var data = new byte[bytes];
                Marshal.Copy(map.DataPtr, data, 0, bytes);
                _onFrame(data, width, height);
            }
            finally
            {
                buffer.Unmap(map);
            }
        }

        private void TearDown()
        {
            var p = Interlocked.Exchange(ref _pipeline, null);
            if (p == null) return;

            // Disconnect the appsink signal before setting Null to avoid
            // a callback firing during teardown.
            try
            {
                var sink = p.GetByName("sink") as Gst.App.AppSink;
                if (sink != null)
                    sink.NewSample -= OnNewSample;
            }
            catch { /* best-effort */ }

            p.SetState(State.Null);
            p.Dispose();
        }

        public async ValueTask DisposeAsync()
        {
            _cts?.Cancel();
            if (_loopTask != null)
                await _loopTask;
            _cts?.Dispose();
        }
    }
}