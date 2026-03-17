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

    private int _nextPort = 5000;

    // id → device
    private readonly ConcurrentDictionary<string, Device> _devices = new();
    // id → port
    private readonly ConcurrentDictionary<string, int> _devicePorts = new();
    // display name → registered id  (used for KS/MF dedup on Windows)
    private readonly ConcurrentDictionary<string, string> _nameToId = new();
    // display names currently being upgraded KS→MF (guard against double-start)
    private readonly ConcurrentHashSet _upgrading = new();

    private static readonly bool IsWindows =
        System.Runtime.InteropServices.RuntimeInformation.IsOSPlatform(
            System.Runtime.InteropServices.OSPlatform.Windows);

    public DeviceMonitorService(
        WebcamManagerService webcamManager,
        ILogger<DeviceMonitorService> logger)
    {
        _webcamManager = webcamManager;
        _logger = logger;
    }

    // ------------------------------------------------------------------ //
    //  Lifecycle                                                           //
    // ------------------------------------------------------------------ //

    protected override async SysTask ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Starting DeviceMonitorService (platform: {Platform})",
            IsWindows ? "Windows" : "Linux");

        _mainLoop = new MainLoop();
        var thread = new System.Threading.Thread(() => _mainLoop.Run())
        {
            IsBackground = true,
            Name = "GStreamerMainLoop"
        };
        thread.Start();

        Gst.Application.Init();

        _monitor = new DeviceMonitor();
        _monitor.AddFilter("Video/Source", null);
        _monitor.Start();

        for (int i = 0; i < 20 && _monitor.Bus == null; i++)
            await SysTask.Delay(50, stoppingToken);

        _bus = _monitor.Bus;
        if (_bus == null)
        {
            _logger.LogError("DeviceMonitor bus is null — aborting");
            return;
        }

        _bus.AddSignalWatch();
        _bus.Message += OnBusMessage;

        _logger.LogInformation("Device monitor running");
        SafeEnumerateExistingDevices();

        try { await SysTask.Delay(System.Threading.Timeout.Infinite, stoppingToken); }
        catch (TaskCanceledException) { }
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

            foreach (var id in _devices.Keys)
                await _webcamManager.StopWebcamAsync(id);
        }
        catch (System.Exception ex)
        {
            _logger.LogError(ex, "Error stopping DeviceMonitorService");
        }
        await base.StopAsync(cancellationToken);
    }

    // ------------------------------------------------------------------ //
    //  Bus                                                                 //
    // ------------------------------------------------------------------ //

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
            case MessageType.DeviceChanged:
                Idle.Add(() =>
                {
                    msg.ParseDeviceChanged(out Device? oldDev, out Device? newDev);
                    HandleDeviceChanged(oldDev, newDev);
                    return false;
                });
                break;
        }
    }

    // ------------------------------------------------------------------ //
    //  Device added                                                        //
    // ------------------------------------------------------------------ //

    private void HandleDeviceAdded(Device? device)
    {
        if (device == null) return;
        if (!IsVideoSource(device)) return;

        var id = device.PathString;
        var name = device.DisplayName ?? "Unknown Camera";

        if (string.IsNullOrEmpty(id)) return;

        // Already registered under this exact id — nothing to do
        if (_devices.ContainsKey(id))
        {
            _logger.LogDebug("Device already registered: {Name} ({Id})", name, id);
            return;
        }

        if (IsWindows)
            HandleWindowsProviderDedup(device, id, name);
        else
            RegisterAndStart(device, id, name);
    }

    // ------------------------------------------------------------------ //
    //  Windows KS/MF deduplication                                        //
    // ------------------------------------------------------------------ //

    private void HandleWindowsProviderDedup(Device device, string id, string name)
    {
        bool isMF = id.Contains("mfdevice", System.StringComparison.OrdinalIgnoreCase);
        bool isKS = id.Contains("ksdevice", System.StringComparison.OrdinalIgnoreCase);

        // MF device arrived — always register it, stopping any existing KS worker first
        if (isMF)
        {
            if (_nameToId.TryGetValue(name, out var existingId))
            {
                bool existingIsMF = existingId.Contains("mfdevice",
                    System.StringComparison.OrdinalIgnoreCase);

                if (existingIsMF)
                {
                    // MF already running for this camera — duplicate event, ignore
                    _logger.LogDebug("MF already registered for {Name} — ignoring duplicate", name);
                    return;
                }

                // KS is running — stop it, then register MF
                _logger.LogInformation("MF arrived for {Name} — stopping KS worker first", name);
                var oldId = existingId;
                _devicePorts.TryGetValue(oldId, out int existingPort);

                // Remove KS tracking immediately so no further events re-trigger it
                _devices.TryRemove(oldId, out _);
                _nameToId.TryRemove(name, out _);
                _devicePorts.TryRemove(oldId, out _);

                _ = SysTask.Run(async () =>
                {
                    try
                    {
                        // Stop KS and wait for hardware to fully release
                        await _webcamManager.StopWebcamAsync(oldId);
                        await SysTask.Delay(800);

                        // Now start MF on the same port
                        RegisterAndStartOnPort(device, id, name, existingPort);
                    }
                    catch (System.Exception ex)
                    {
                        _logger.LogError(ex, "Failed during KS→MF upgrade for {Name}", name);
                    }
                });
                return;
            }

            // No existing entry — fresh MF registration
            RegisterAndStart(device, id, name);
            return;
        }

        // KS device arrived
        if (isKS)
        {
            // Wait to see if MF arrives within 500ms before committing to KS
            _ = SysTask.Run(async () =>
            {
                await SysTask.Delay(500);

                // If MF has already registered by now, skip KS entirely
                if (_nameToId.TryGetValue(name, out var laterId) &&
                    laterId.Contains("mfdevice", System.StringComparison.OrdinalIgnoreCase))
                {
                    _logger.LogDebug("MF arrived in time — skipping KS for {Name}", name);
                    return;
                }

                // Check again that nothing registered this id in the meantime
                if (_devices.ContainsKey(id))
                {
                    _logger.LogDebug("Device already registered during KS wait for {Name}", name);
                    return;
                }

                _logger.LogInformation("No MF arrived — falling back to KS for {Name}", name);
                RegisterAndStart(device, id, name);
            });
            return;
        }

        // Neither KS nor MF (unlikely on Windows but handle gracefully)
        RegisterAndStart(device, id, name);
    }

    // ------------------------------------------------------------------ //
    //  Device removed                                                      //
    // ------------------------------------------------------------------ //

    private void HandleDeviceRemoved(Device? device)
    {
        if (device == null) return;

        var id = device.PathString;
        var name = device.DisplayName ?? "";

        if (string.IsNullOrEmpty(id) || !_devices.TryRemove(id, out _)) return;

        _nameToId.TryRemove(name, out _);
        _devicePorts.TryRemove(id, out _);

        _logger.LogInformation("Camera removed: {Name} | ID: {Id}", name, id);

        _ = SysTask.Run(async () =>
        {
            try { await _webcamManager.StopWebcamAsync(id); }
            catch (System.Exception ex)
            { _logger.LogError(ex, "Failed stopping webcam for {Id}", id); }
        });
    }

    // ------------------------------------------------------------------ //
    //  Device changed                                                      //
    // ------------------------------------------------------------------ //

    private void HandleDeviceChanged(Device? oldDevice, Device? newDevice)
    {
        if (oldDevice == null || newDevice == null) return;

        var id = oldDevice.PathString;
        var name = oldDevice.DisplayName ?? "";

        if (string.IsNullOrEmpty(id) || !_devices.ContainsKey(id)) return;

        _logger.LogInformation("Camera changed: {Name} — restarting pipeline", name);
        _devicePorts.TryGetValue(id, out int port);

        _ = SysTask.Run(async () =>
        {
            try
            {
                await _webcamManager.StopWebcamAsync(id);
                _devices.TryRemove(id, out _);
                _nameToId.TryRemove(name, out _);
                _devicePorts.TryRemove(id, out _);

                await SysTask.Delay(300);

                var newId = newDevice.PathString ?? id;
                RegisterAndStartOnPort(newDevice, newId, name, port);
            }
            catch (System.Exception ex)
            { _logger.LogError(ex, "Failed restarting webcam after change for {Id}", id); }
        });
    }

    // ------------------------------------------------------------------ //
    //  Registration                                                        //
    // ------------------------------------------------------------------ //

    private void RegisterAndStart(Device device, string id, string name)
    {
        int port = Interlocked.Increment(ref _nextPort);
        RegisterAndStartOnPort(device, id, name, port);
    }

    private void RegisterAndStartOnPort(Device device, string id, string name, int port)
    {
        _devices[id] = device;
        _nameToId[name] = id;
        _devicePorts[id] = port;

        _logger.LogInformation("Camera connected: {Name} | ID: {Id} | Port: {Port}",
            name, id, port);

        _ = SysTask.Run(async () =>
        {
            try { await _webcamManager.StartWebcamAsync(device, "127.0.0.1", port); }
            catch (System.Exception ex)
            { _logger.LogError(ex, "Failed to start webcam for {Name}", name); }
        });
    }

    // ------------------------------------------------------------------ //
    //  Startup enumeration                                                 //
    // ------------------------------------------------------------------ //

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
        { _logger.LogError(ex, "Error enumerating existing devices"); }
    }

    // ------------------------------------------------------------------ //
    //  Helpers                                                             //
    // ------------------------------------------------------------------ //

    private static bool IsVideoSource(Device device)
    {
        var cls = device.DeviceClass ?? "";
        var name = (device.DisplayName ?? "").ToLowerInvariant();

        if (!cls.Contains("Video", System.StringComparison.OrdinalIgnoreCase)) return false;
        if (cls.Contains("Sink", System.StringComparison.OrdinalIgnoreCase)) return false;
        if (name.Contains("monitor") || name.Contains("virtual") ||
            name.Contains("screen")) return false;

        return true;
    }

    // Minimal thread-safe hash set (ConcurrentDictionary<string,byte> wrapper)
    private sealed class ConcurrentHashSet
    {
        private readonly ConcurrentDictionary<string, byte> _inner = new();
        public void Add(string key) => _inner.TryAdd(key, 0);
        public void Remove(string key) => _inner.TryRemove(key, out _);
        public bool Contains(string key) => _inner.ContainsKey(key);
    }
}