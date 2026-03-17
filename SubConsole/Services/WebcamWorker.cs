using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
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

        private const int MaxRestarts = 10;

        public WebcamWorker(Gst.Device device, string host, int port, ILogger logger)
        {
            _device = device;
            _deviceName = device.DisplayName ?? "Unknown";
            _host = host;
            _port = port;
            _logger = logger;
        }

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
                try
                {
                    Gst.Application.Init();
                    BuildPipeline();

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
                        var waitReturn = _pipeline.GetState(out State current, out State pending, 10 * Gst.Constants.SECOND);
                        _logger.LogInformation("[{Device}] GetState result: {Return} | current={Current} pending={Pending}",
                            _deviceName, waitReturn, current, pending);

                        if (waitReturn == StateChangeReturn.Failure)
                        {
                            DrainBusErrors();
                            throw new Exception("Async state change to Playing failed");
                        }
                    }

                    _logger.LogInformation("[{Device}] Pipeline is PLAYING — monitoring bus", _deviceName);

                    var bus = _pipeline.Bus!;
                    bus.AddSignalWatch();
                    bus.Message += OnBusMessage;

                    while (!token.IsCancellationRequested)
                        await SysTask.Delay(100, token);

                    break;
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
                    await SysTask.Delay(500, CancellationToken.None);
                }
            }

            if (restartCount >= MaxRestarts)
                _logger.LogError("[{Device}] Too many failures — stopping", _deviceName);

            TearDownPipeline();

            await _semaphore.WaitAsync();
            try { _pipelineTask = null; }
            finally { _semaphore.Release(); }
        }

        // ------------------------------------------------------------------ //
        //  Pipeline construction                                               //
        // ------------------------------------------------------------------ //

        private void BuildPipeline()
        {
            TearDownPipeline();

            _pipeline = new Pipeline($"webcam-{_deviceName}");

            var source = _device.CreateElement("src")
                ?? throw new Exception($"[{_deviceName}] device.CreateElement() returned null");

            _logger.LogInformation("[{Device}] Source element created via device.CreateElement()", _deviceName);

            var convert = ElementFactory.Make("videoconvert", "convert")
                ?? throw new Exception("Failed to create videoconvert — install gstreamer1-plugins-base");

            string? encoderFactory = FindAvailableEncoder();
            if (encoderFactory == null)
                throw new Exception(
                    "No H.264 encoder found. " +
                    "Windows: ensure gstreamer is fully installed. " +
                    "Linux: install gstreamer1-plugin-ugly (x264enc) or gstreamer1-plugin-openh264");

            var encoder = ElementFactory.Make(encoderFactory, "encoder")
                ?? throw new Exception($"Failed to create encoder: {encoderFactory}");

            ConfigureEncoder(encoder, encoderFactory);
            _logger.LogInformation("[{Device}] Using encoder: {Encoder}", _deviceName, encoderFactory);

            var parse = ElementFactory.Make("h264parse", "parse")
                ?? throw new Exception("Failed to create h264parse — install gstreamer1-plugins-bad");

            // RTP payloader — this is what was missing
            var rtp = ElementFactory.Make("rtph264pay", "rtp")
                ?? throw new Exception("Failed to create rtph264pay — install gstreamer1-plugins-good");
            rtp["config-interval"] = -1;  // send SPS/PPS with every keyframe
            rtp["pt"] = 96;  // payload type must match receiver caps
            rtp["mtu"] = (uint)1400;

            var sink = ElementFactory.Make("udpsink", "sink")
                ?? throw new Exception("Failed to create udpsink");
            sink["host"] = _host;
            sink["port"] = _port;
            sink["sync"] = false;

            _pipeline.Add(source, convert, encoder, parse, rtp, sink);

            if (!Element.Link(source, convert, encoder, parse, rtp, sink))
                throw new Exception("Failed to link pipeline elements");

            _logger.LogInformation("[{Device}] Pipeline built successfully", _deviceName);
        }

        private static string? FindAvailableEncoder()
        {
            // Preference order — first available wins
            foreach (string factory in new[] { "x264enc", "openh264enc", "nvh264enc", "vtenc_h264" })
            {
                var test = ElementFactory.Make(factory, "test-enc");
                if (test != null)
                {
                    test.Dispose();
                    return factory;
                }
            }
            return null;
        }

        private void ConfigureEncoder(Element enc, string factory)
        {
            switch (factory)
            {
                case "x264enc":
                    enc["tune"] = (uint)4;   // zerolatency
                    enc["speed-preset"] = (uint)1;   // ultrafast
                    enc["bitrate"] = (uint)1200;
                    enc["key-int-max"] = (uint)30;
                    break;

                case "openh264enc":
                    enc["bitrate"] = (uint)(1200 * 1000); // openh264enc uses bps not kbps
                    enc["gop-size"] = 30;
                    enc["complexity"] = 0; // low
                    break;

                case "nvh264enc":   // NVIDIA NVENC
                    enc["bitrate"] = (uint)1200;
                    enc["preset"] = 1;    // low-latency
                    enc["zerolatency"] = true;
                    break;

                case "vtenc_h264":  // Apple VideoToolbox (macOS)
                    enc["bitrate"] = (uint)1200;
                    enc["realtime"] = true;
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
                        _logger.LogError("[{Device}] Drain error: {Message}",
                            _deviceName, msg.Structure?.ToString() ?? "no details");
                        break;
                    case MessageType.Warning:
                        _logger.LogWarning("[{Device}] Drain warning: {Message}",
                            _deviceName, msg.Structure?.ToString() ?? "no details");
                        break;
                    case MessageType.StateChanged:
                        msg.ParseStateChanged(out State old, out State now, out State pending);
                        _logger.LogDebug("[{Device}] Drain state: {Old} → {Now} (pending: {Pending})",
                            _deviceName, old, now, pending);
                        break;
                }
            }
        }

        private void OnBusMessage(object? sender, MessageArgs args)
        {
            var msg = args.Message;
            switch (msg.Type)
            {
                case MessageType.Error:
                    _logger.LogError("[{Device}] GStreamer error: {Message}",
                        _deviceName, msg.Structure?.ToString() ?? "no details");
                    TearDownPipeline();
                    break;

                case MessageType.Warning:
                    _logger.LogWarning("[{Device}] GStreamer warning: {Message}",
                        _deviceName, msg.Structure?.ToString() ?? "no details");
                    break;

                case MessageType.Eos:
                    _logger.LogInformation("[{Device}] End of stream", _deviceName);
                    TearDownPipeline();
                    break;

                case MessageType.StateChanged:
                    if (msg.Src == _pipeline)
                    {
                        msg.ParseStateChanged(out State old, out State now, out State pending);
                        _logger.LogInformation("[{Device}] Pipeline state: {Old} → {Now} (pending: {Pending})",
                            _deviceName, old, now, pending);
                    }
                    break;
            }
        }
        // ------------------------------------------------------------------ //
        //  Teardown                                                            //
        // ------------------------------------------------------------------ //

        private void TearDownPipeline()
        {
            if (_pipeline == null) return;
            _pipeline.SetState(State.Null);
            _pipeline.Dispose();
            _pipeline = null;
        }
    }
}