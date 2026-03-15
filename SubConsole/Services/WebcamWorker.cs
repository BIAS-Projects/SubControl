using Microsoft.Extensions.Logging;
using System;
using System.Threading;
using SysTask = System.Threading.Tasks.Task;
using Gst;

namespace SubConsole.Services;

public class WebcamWorker : IAsyncDisposable
{
    private readonly string _deviceId;
    private readonly string _deviceName;
    private readonly string _host;
    private readonly int _port;
    private readonly ILogger _logger;

    private Pipeline? _pipeline;
    private CancellationTokenSource? _cts;
    private SysTask? _pipelineTask;
    private readonly SemaphoreSlim _semaphore = new(1, 1);

    private const int X264_PRESET_ULTRAFAST = 1;
    private const int X264_TUNE_ZEROLATENCY = 4;
    private const int X264_PRESET_VERYFAST = 3;   // was ULTRAFAST=1; better quality per bit

    public WebcamWorker(string deviceId, string deviceName, string host, int port, ILogger logger)
    {
        _deviceId = deviceId;
        _deviceName = deviceName;
        _host = host;
        _port = port;
        _logger = logger;
    }

    public async SysTask StartAsync(CancellationToken parentToken)
    {
        await _semaphore.WaitAsync(parentToken);
        try
        {
            if (_pipelineTask != null) return;
            _cts = CancellationTokenSource.CreateLinkedTokenSource(parentToken);
            await SysTask.Delay(300, parentToken);
            _pipelineTask = SysTask.Run(() => RunPipelineAsync(_cts.Token));
        }
        finally
        {
            _semaphore.Release();
        }
    }

    private async SysTask RunPipelineAsync(CancellationToken token)
    {
        try
        {
            Gst.Application.Init();

            _pipeline = BuildPipeline();
            if (_pipeline == null)
            {
                _logger.LogError("Pipeline construction failed for {Device}", _deviceName);
                return;
            }

            _logger.LogInformation("Starting webcam pipeline: {Device}", _deviceName);

            var stateChange = _pipeline.SetState(State.Playing);
            if (stateChange == StateChangeReturn.Failure)
            {
                _logger.LogError("Failed to set pipeline to Playing for {Device}", _deviceName);
                return;
            }

            var bus = _pipeline.Bus;
            while (!token.IsCancellationRequested)
            {
                var msg = bus.TimedPopFiltered(
                    100 * Gst.Constants.MSECOND,
                    MessageType.Error | MessageType.Eos | MessageType.StateChanged);

                if (msg == null) continue;

                switch (msg.Type)
                {
                    case MessageType.Error:
                        msg.ParseError(out GLib.GException err, out string debug);
                        _logger.LogError("GStreamer error on {Device}: {Error} | Debug: {Debug}",
                            _deviceName, err.Message, debug);
                        return;

                    case MessageType.Eos:
                        _logger.LogInformation("EOS received for {Device}", _deviceName);
                        return;

                    case MessageType.StateChanged:
                        if (msg.Src == _pipeline)
                        {
                            msg.ParseStateChanged(out State oldState, out State newState, out _);
                            _logger.LogDebug("Pipeline state: {Old} → {New}", oldState, newState);
                        }
                        break;
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Pipeline crashed for {Device}", _deviceName);
        }
        finally
        {
            await StopPipelineAsync();
        }
    }

    private Pipeline? BuildPipeline()
    {

        // ── Quality settings ─────────────────────────────────────────────────────




    var srcFactory = GStreamerPlatform.VideoSourceFactory(_deviceId);
        var src = ElementFactory.Make(srcFactory, "src");

        if (src == null)
        {
            _logger.LogError("Failed to create '{Factory}'", srcFactory);
            return null;
        }

        // v4l2src uses a device path string, MF/KS use an integer index
        if (srcFactory == "v4l2src")
            src["device"] = "/dev/video0"; // override per-device if needed
        else
            src["device-index"] = 0;

        _logger.LogInformation("Opening camera via {Factory}", srcFactory);

        var capsFilter = ElementFactory.Make("capsfilter", "capsfilter");
        var convert = ElementFactory.Make("videoconvert", "convert");
        var enc = ElementFactory.Make("x264enc", "enc");
        var parse = ElementFactory.Make("h264parse", "parse");
        var rtp = ElementFactory.Make("rtph264pay", "rtp");
        var sink = ElementFactory.Make("udpsink", "sink");

        if (capsFilter == null) { _logger.LogError("Failed to create 'capsfilter'"); return null; }
        if (convert == null) { _logger.LogError("Failed to create 'videoconvert'"); return null; }
        if (enc == null) { _logger.LogError("Failed to create 'x264enc'"); return null; }
        if (parse == null) { _logger.LogError("Failed to create 'h264parse'"); return null; }
        if (rtp == null) { _logger.LogError("Failed to create 'rtph264pay'"); return null; }
        if (sink == null) { _logger.LogError("Failed to create 'udpsink'"); return null; }

        //capsFilter["caps"] = Caps.FromString("video/x-raw");

        //enc["bitrate"] = (uint)800;
        //enc["speed-preset"] = X264_PRESET_ULTRAFAST;
        //enc["tune"] = X264_TUNE_ZEROLATENCY;
        //enc["key-int-max"] = (uint)30;

        //rtp["config-interval"] = -1;
        //rtp["pt"] = 96;
        //rtp["mtu"] = (uint)1400;

        // Constrain capture to explicit resolution + framerate.
        // The driver picks arbitrarily if left as "video/x-raw" with no constraints.
        capsFilter["caps"] = Caps.FromString(
            "video/x-raw,width=1280,height=720,framerate=30/1");

        enc["bitrate"] = (uint)4000;            // was 800 — 5× more bits = far less blocking
        enc["speed-preset"] = X264_PRESET_VERYFAST;  // was ultrafast — better quality at mild CPU cost
        enc["tune"] = X264_TUNE_ZEROLATENCY; // keep — prevents B-frames, keeps latency low
        enc["key-int-max"] = (uint)30;              // IDR every second at 30fps — fine for live

        rtp["config-interval"] = -1;
        rtp["pt"] = 96;
        rtp["mtu"] = (uint)60000;  // was 1400; large MTU safe on loopback/LAN, fewer UDP fragments


        parse["config-interval"] = -1;



        sink["host"] = _host;
        sink["port"] = _port;
        sink["sync"] = false;

        var pipeline = new Pipeline("webcam-pipeline");
        pipeline.Add(src, capsFilter, convert, enc, parse, rtp, sink);

        if (!Element.Link(src, capsFilter, convert, enc, parse, rtp, sink))
        {
            _logger.LogError("Failed to link pipeline");
            return null;
        }

        return pipeline;
    }
    public async SysTask StopAsync()
    {
        await _semaphore.WaitAsync();
        try { _cts?.Cancel(); }
        finally { _semaphore.Release(); }

        if (_pipelineTask != null)
            await _pipelineTask;
    }

    private async SysTask StopPipelineAsync()
    {
        await _semaphore.WaitAsync();
        try
        {
            if (_pipeline == null) return;
            _pipeline.SetState(State.Null);
            _pipeline.Dispose();
            _pipeline = null;
            _pipelineTask = null;
        }
        finally
        {
            _semaphore.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync();
        _cts?.Dispose();
        _semaphore.Dispose();
    }
}