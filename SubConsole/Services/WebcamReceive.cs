using Gst;
using Gst.App;
using Gst.Video;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System;
using System.Threading;
using SysTask = System.Threading.Tasks.Task;

namespace SubConsole.Services;

public class WebcamReceiverService : BackgroundService
{
    private readonly ILogger<WebcamReceiverService> _logger;
    private readonly string _host;
    private readonly int _port;

    private Pipeline? _pipeline;

    public event Action<VideoFrame>? FrameReceived;

    public WebcamReceiverService(ILogger<WebcamReceiverService> logger, string host = "127.0.0.1", int port = 5001)
    {
        _logger = logger;
        _host = host;
        _port = port;
    }

    protected override async SysTask ExecuteAsync(CancellationToken stoppingToken)
    {
        _pipeline = BuildPipeline();

        if (_pipeline == null)
        {
            _logger.LogError("Receiver pipeline construction failed for {Host}:{Port}", _host, _port);
            return;
        }

        _logger.LogInformation("Starting webcam receiver on {Host}:{Port}", _host, _port);

        var stateChange = _pipeline.SetState(State.Playing);
        if (stateChange == StateChangeReturn.Failure)
        {
            _logger.LogError("Failed to set receiver pipeline to Playing");
            return;
        }

        var bus = _pipeline.Bus;

        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                var msg = bus.TimedPopFiltered(
                    100 * Gst.Constants.MSECOND,
                    MessageType.Error | MessageType.Eos | MessageType.StateChanged);

                if (msg == null) continue;

                switch (msg.Type)
                {
                    case MessageType.Error:
                        msg.ParseError(out GLib.GException err, out string debug);
                        _logger.LogError("GStreamer receiver error: {Error} | Debug: {Debug}",
                            err.Message, debug);
                        return;

                    case MessageType.Eos:
                        _logger.LogInformation("Receiver EOS on {Host}:{Port}", _host, _port);
                        return;

                    case MessageType.StateChanged:
                        if (msg.Src == _pipeline)
                        {
                            msg.ParseStateChanged(out State oldState, out State newState, out _);
                            _logger.LogDebug("Receiver pipeline state: {Old} → {New}", oldState, newState);
                        }
                        break;
                }
            }
        }
        finally
        {
            _logger.LogInformation("Stopping webcam receiver on {Host}:{Port}", _host, _port);
            _pipeline.SetState(State.Null);
            _pipeline.Dispose();
            _pipeline = null;
        }
    }

    private Pipeline? BuildPipeline()
    {
        var src = ElementFactory.Make("udpsrc", "src");
        var depay = ElementFactory.Make("rtph264depay", "depay");
        var parse = ElementFactory.Make("h264parse", "parse");
        var dec = GStreamerPlatform.CreateH264Decoder();
        var convert = ElementFactory.Make("videoconvert", "convert");
        var sink = new AppSink("appsink");

        if (src == null) { _logger.LogError("Failed to create 'udpsrc'"); return null; }
        if (depay == null) { _logger.LogError("Failed to create 'rtph264depay'"); return null; }
        if (parse == null) { _logger.LogError("Failed to create 'h264parse'"); return null; }
        if (dec == null) { _logger.LogError("Failed to create any H264 decoder"); return null; }
        if (convert == null) { _logger.LogError("Failed to create 'videoconvert'"); return null; }

        src["address"] = "0.0.0.0";
        src["port"] = _port;
        src["caps"] = Caps.FromString(
            "application/x-rtp,media=video,clock-rate=90000,encoding-name=H264,payload=96");

        parse["config-interval"] = -1;

        sink["caps"] = Caps.FromString("video/x-raw,format=RGB");
        sink["emit-signals"] = true;
        sink["max-buffers"] = (uint)2;
        sink["drop"] = true;
        sink["sync"] = false;

        sink.NewSample += OnNewSample;

        var pipeline = new Pipeline("webcam-receiver");
        pipeline.Add(src, depay, parse, dec, convert, sink);

        if (!Element.Link(src, depay, parse, dec, convert, sink))
        {
            _logger.LogError("Failed to link receiver pipeline");
            return null;
        }

        return pipeline;
    }

    private void OnNewSample(object sender, GLib.SignalArgs args)
    {
        if (sender is not AppSink sink) return;

        using var sample = sink.TryPullSample(0);
        if (sample == null) return;

        var buffer = sample.Buffer;
        var caps = sample.Caps;

        var structure = caps.GetStructure(0);
        structure.GetInt("width", out int width);
        structure.GetInt("height", out int height);

        buffer.Map(out MapInfo mapInfo, MapFlags.Read);
        var data = new byte[mapInfo.Size];
        System.Runtime.InteropServices.Marshal.Copy(mapInfo.DataPtr, data, 0, (int)mapInfo.Size);
        buffer.Unmap(mapInfo);

        FrameReceived?.Invoke(new VideoFrame(width, height, data));
    }
}