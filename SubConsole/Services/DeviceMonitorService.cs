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

    // Tracks display names already claimed to prevent KS+MF double-open
    private readonly ConcurrentDictionary<string, string> _nameToId = new();

    public DeviceMonitorService(WebcamManagerService webcamManager, ILogger<DeviceMonitorService> logger)
    {
        _webcamManager = webcamManager;
        _logger = logger;
    }

    protected override async SysTask ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Starting DeviceMonitorService");

        _mainLoop = new MainLoop();
        var thread = new System.Threading.Thread(() => _mainLoop.Run())
        {
            IsBackground = true,
            Name = "GStreamerMainLoop"
        };
        thread.Start();

        _monitor = new DeviceMonitor();
        _monitor.AddFilter("Video", null);
        _monitor.Start();

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
        if (device == null) return;

        if (string.IsNullOrEmpty(device.DeviceClass) || !device.DeviceClass.Contains("Video"))
            return;

        if (!string.IsNullOrEmpty(device.DisplayName) &&
            device.DisplayName.ToLower().Contains("monitor"))
            return;

        var id = device.PathString;
        var name = device.DisplayName ?? "Unknown Camera";

        if (string.IsNullOrEmpty(id) || _devices.ContainsKey(id))
            return;

        // Prefer MF provider over KS — if the same camera name already has an
        // MF entry, skip the KS duplicate. If we have a KS entry and MF arrives,
        // replace it.
        bool isMF = id.Contains("mfdevice", System.StringComparison.OrdinalIgnoreCase);
        bool isKS = id.Contains("ksdevice", System.StringComparison.OrdinalIgnoreCase);

        if (_nameToId.TryGetValue(name, out var existingId))
        {
            bool existingIsMF = existingId.Contains("mfdevice", System.StringComparison.OrdinalIgnoreCase);

            if (existingIsMF)
            {
                // Already have MF for this camera — skip the KS duplicate
                _logger.LogDebug("Skipping duplicate KS device for {Name} — MF already registered", name);
                return;
            }

            if (isMF && !existingIsMF)
            {
                // Upgrade from KS → MF: stop old worker and replace
                _logger.LogInformation("Upgrading {Name} from KS to MF provider", name);
                _ = SysTask.Run(async () => await _webcamManager.StopWebcamAsync(existingId));
                _devices.TryRemove(existingId, out _);
                _nameToId.TryRemove(name, out _);
            }
        }

        if (isKS && !isMF)
        {
            // Hold off briefly — MF provider usually arrives within 200ms and is preferred
            _ = SysTask.Run(async () =>
            {
                await SysTask.Delay(300);

                // If MF has arrived by now for this name, skip KS entirely
                if (_nameToId.TryGetValue(name, out var laterId) &&
                    laterId.Contains("mfdevice", System.StringComparison.OrdinalIgnoreCase))
                {
                    _logger.LogDebug("MF device arrived in time — skipping KS for {Name}", name);
                    return;
                }

                RegisterAndStart(device, id, name);
            });
            return;
        }

        RegisterAndStart(device, id, name);
    }

    private void RegisterAndStart(Device device, string id, string name)
    {
        var port = Interlocked.Increment(ref _port);

        _devices[id] = device;
        _nameToId[name] = id;

        _logger.LogInformation("Camera connected: {Name} | ID: {Id} | Port: {Port}", name, id, port);

        _ = SysTask.Run(async () =>
        {
            try
            {
                await _webcamManager.StartWebcamAsync(device, "127.0.0.1", port);
          //      await _webcamManager.StartWebcamAsync(device, "0.0.0.0", port);
            }
            catch (System.Exception ex)
            {
                _logger.LogError(ex, "Failed to start webcam pipeline for {Name}", name);
            }
        });
    }

    private void HandleDeviceRemoved(Device? device)
    {
        if (device == null) return;

        var id = device.PathString;
        var name = device.DisplayName ?? "";

        if (string.IsNullOrEmpty(id) || !_devices.TryRemove(id, out _))
            return;

        _nameToId.TryRemove(name, out _);

        _logger.LogInformation("Camera removed: {Name} | ID: {Id}", name, id);

        _ = SysTask.Run(async () =>
        {
            try
            {
                await _webcamManager.StopWebcamAsync(id);
            }
            catch (System.Exception ex)
            {
                _logger.LogError(ex, "Failed stopping webcam pipeline for {Id}", id);
            }
        });
    }

    private void SafeEnumerateExistingDevices()
    {
        if (_monitor == null) return;
        try
        {
            foreach (var obj in _monitor.Devices)
                if (obj is Device device)
                    HandleDeviceAdded(device);
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
            _monitor?.Stop();
            _monitor?.Dispose();
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