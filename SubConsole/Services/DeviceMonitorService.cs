using Gst;
using GLib;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System.Collections.Concurrent;
using System.Threading;
using SysTask = System.Threading.Tasks.Task;

namespace SubConsole.Services;

public class DeviceMonitorService : BackgroundService
{
    private readonly WebcamManagerService _webcamManager;
    private readonly ILogger<DeviceMonitorService> _logger;

    private DeviceMonitor? _monitor;
    private Bus? _bus;
    private MainLoop? _mainLoop;

    private int _port = 5000;
    private readonly ConcurrentDictionary<string, Device> _devices = new();

    public DeviceMonitorService(WebcamManagerService webcamManager, ILogger<DeviceMonitorService> logger)
    {
        _webcamManager = webcamManager;
        _logger = logger;
    }

    protected override async SysTask ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Starting DeviceMonitorService");

        // Start GLib MainLoop on a background thread
        _mainLoop = new MainLoop();
        var thread = new System.Threading.Thread(() => _mainLoop.Run())
        {
            IsBackground = true,
            Name = "GStreamerMainLoop"
        };
        thread.Start();

        // Start DeviceMonitor for video sources
        _monitor = new DeviceMonitor();
        _monitor.AddFilter("Video", null); // detect video sources
        _monitor.Start();

        // Wait for Bus
        for (int i = 0; i < 20 && _monitor.Bus == null; i++)
            await SysTask.Delay(50, stoppingToken);

        _bus = _monitor.Bus;
        if (_bus == null)
        {
            _logger.LogError("DeviceMonitor bus is null");
            return;
        }

        _bus.AddSignalWatch();
        _bus.Message += OnBusMessage;

        _logger.LogInformation("Device monitor running");

        // Handle existing devices at startup
        SafeEnumerateExistingDevices();

        try
        {
            await SysTask.Delay(System.Threading.Timeout.Infinite, stoppingToken);
        }
        catch (TaskCanceledException) { }
    }

    private void OnBusMessage(object sender, MessageArgs args)
    {
        var msg = args.Message;
        switch (msg.Type)
        {
            case MessageType.DeviceAdded:
                Idle.Add(() => { HandleDeviceAdded(msg.ParseDeviceAdded()); return false; });
                break;
            case MessageType.DeviceRemoved:
                Idle.Add(() => { HandleDeviceRemoved(msg.ParseDeviceRemoved()); return false; });
                break;
        }
    }

    private void HandleDeviceAdded(Device? device)
    {
        if (device == null)
            return;

        if (string.IsNullOrEmpty(device.DeviceClass) || !device.DeviceClass.Contains("Video"))
            return;

        if (!string.IsNullOrEmpty(device.DisplayName) &&
            device.DisplayName.ToLower().Contains("monitor"))
            return;

        var id = device.PathString;
        if (string.IsNullOrEmpty(id) || _devices.ContainsKey(id))
            return;

        var name = device.DisplayName ?? "Unknown Camera";
        var port = Interlocked.Increment(ref _port);

        _devices[id] = device;

        _logger.LogInformation("Camera connected: {Name} | ID: {Id} | Port: {Port}", name, id, port);

        _ = SysTask.Run(async () =>
        {
            try
            {
                await _webcamManager.StartWebcamAsync(device, "0.0.0.0", port);
            }
            catch (System.Exception ex)
            {
                _logger.LogError(ex, "Failed to start webcam pipeline");
            }
        });
    }

    private void HandleDeviceRemoved(Device? device)
    {
        if (device == null)
            return;

        var id = device.PathString;
        if (string.IsNullOrEmpty(id) || !_devices.TryRemove(id, out var _))
            return;

        _logger.LogInformation("Camera removed: ID: {Id}", id);

        _ = SysTask.Run(async () =>
        {
            try
            {
                await _webcamManager.StopWebcamAsync(id);
            }
            catch (System.Exception ex)
            {
                _logger.LogError(ex, "Failed stopping webcam pipeline");
            }
        });
    }

    private void SafeEnumerateExistingDevices()
    {
        if (_monitor == null) return;

        try
        {
            foreach (var obj in _monitor.Devices)
            {
                if (obj is Device device)
                    HandleDeviceAdded(device);
            }
        }
        catch (System.Exception ex)
        {
            _logger.LogError(ex, "Error enumerating existing devices");
        }
    }

    public override async SysTask StopAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("Stopping DeviceMonitorService");

        try
        {
            if (_bus != null)
            {
                _bus.Message -= OnBusMessage;
                _bus.RemoveSignalWatch();
            }

            if (_monitor != null)
            {
                _monitor.Stop();
                _monitor.Dispose();
            }

            _mainLoop?.Quit();

            foreach (var deviceId in _devices.Keys)
                await _webcamManager.StopWebcamAsync(deviceId);
        }
        catch (System.Exception ex)
        {
            _logger.LogError(ex, "Error stopping DeviceMonitorService");
        }

        await base.StopAsync(cancellationToken);
    }
}
