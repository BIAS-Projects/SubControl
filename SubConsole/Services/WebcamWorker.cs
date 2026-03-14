using Microsoft.Extensions.Logging;
using System;
using System.Threading;
using SysTask = System.Threading.Tasks.Task;
using Gst;

namespace SubConsole.Services;

public class WebcamWorker : IAsyncDisposable
{
    private readonly Device _device;
    private readonly string _host;
    private readonly int _port;
    private readonly ILogger _logger;

    private Pipeline? _pipeline;
    private CancellationTokenSource? _cts;
    private SysTask? _pipelineTask;
    private readonly SemaphoreSlim _semaphore = new(1, 1);

    // x264enc GLib enum integer values
    private const int X264_PRESET_ULTRAFAST = 1; // GstX264EncPreset
    private const int X264_TUNE_ZEROLATENCY = 4; // GstX264EncTune bitmask

    public WebcamWorker(Device device, string host, int port, ILogger logger)
    {
        _device = device;
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
                _logger.LogError("Pipeline construction failed for {Device}", _device.DisplayName);
                return;
            }

            _logger.LogInformation("Starting webcam pipeline: {Device}", _device.DisplayName);

            var stateChange = _pipeline.SetState(State.Playing);
            if (stateChange == StateChangeReturn.Failure)
            {
                _logger.LogError("Failed to set pipeline to Playing for {Device}", _device.DisplayName);
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
                            _device.DisplayName, err.Message, debug);
                        return;

                    case MessageType.Eos:
                        _logger.LogInformation("EOS received for {Device}", _device.DisplayName);
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
            _logger.LogError(ex, "Pipeline crashed for {Device}", _device.DisplayName);
        }
        finally
        {
            await StopPipelineAsync();
        }
    }

    private Pipeline? BuildPipeline()
    {
        Element? src;
        try
        {
            src = _device.CreateElement(null);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to create source element for {Device}", _device.DisplayName);
            return null;
        }

        if (src == null)
        {
            _logger.LogError("Device returned null source element for {Device}", _device.DisplayName);
            return null;
        }

        var convert = ElementFactory.Make("videoconvert", "convert");
        var enc = ElementFactory.Make("x264enc", "enc");
        // Wrap raw H.264 in a bytestream so TCP receiver can find NAL boundaries
        var parse = ElementFactory.Make("h264parse", "parse");
        var sink = ElementFactory.Make("tcpserversink", "sink");

        if (convert == null) { _logger.LogError("Failed to create 'videoconvert'"); return null; }
        if (enc == null) { _logger.LogError("Failed to create 'x264enc' — is gst-plugins-ugly installed?"); return null; }
        if (parse == null) { _logger.LogError("Failed to create 'h264parse' — is gst-plugins-bad installed?"); return null; }
        if (sink == null) { _logger.LogError("Failed to create 'tcpserversink'"); return null; }

        // x264enc — integer enum values, NOT strings
        enc["bitrate"] = (uint)800;
        enc["speed-preset"] = X264_PRESET_ULTRAFAST;
        enc["tune"] = X264_TUNE_ZEROLATENCY;
        enc["key-int-max"] = (uint)30;

        // h264parse — emit Access Unit delimiters so receiver can resync
        parse["config-interval"] = -1; // inject SPS/PPS before every keyframe

        // tcpserversink
        sink["host"] = _host;
        sink["port"] = _port;
        sink["sync"] = false;

        var pipeline = new Pipeline("webcam-pipeline");

        bool isMF = src.Name.StartsWith("mf", StringComparison.OrdinalIgnoreCase)
                 || _device.DeviceClass.Contains("Video");

        if (isMF)
        {
            var capsFilter = ElementFactory.Make("capsfilter", "capsfilter");
            if (capsFilter == null)
            {
                _logger.LogError("Failed to create 'capsfilter'");
                return null;
            }

            var caps = Caps.FromString("video/x-raw,format=NV12,width=1280,height=720,framerate=30/1");
            capsFilter["caps"] = caps;

            pipeline.Add(src, capsFilter, convert, enc, parse, sink);

            if (!Element.Link(src, capsFilter, convert, enc, parse, sink))
            {
                _logger.LogError("Failed to link MF pipeline: src → capsfilter → convert → enc → parse → sink");
                return null;
            }
        }
        else
        {
            pipeline.Add(src, convert, enc, parse, sink);

            if (!Element.Link(src, convert, enc, parse, sink))
            {
                _logger.LogError("Failed to link pipeline: src → convert → enc → parse → sink");
                return null;
            }
        }

        return pipeline;
    }

    public async SysTask StopAsync()
    {
        await _semaphore.WaitAsync();
        try
        {
            _cts?.Cancel();
        }
        finally
        {
            _semaphore.Release();
        }

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