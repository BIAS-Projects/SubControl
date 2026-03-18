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
    // display name → semaphore serialising concurrent MF arrival events for the same camera
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _nameUpgradeLocks = new();
    // display name → caps string discovered by KS probe, used as hint for MF replacement worker
    private readonly ConcurrentDictionary<string, (string caps, bool isGray)> _nameToCapsHint = new();

    // Known thermal/speciality cameras that cannot stream on MF.
    // Keyed by display-name substring (case-insensitive).  The value is the
    // caps string to use directly, bypassing all probing.
    // Add entries here for any camera whose MF source advertises generic caps
    // it cannot actually stream (thermal cameras, machine-vision cameras, etc.)
    private static readonly (string nameSubstring, string caps, bool isGray)[] KnownThermalDevices =
    {
        ("FLIR",  "video/x-raw,format=GRAY16_LE,width=320,height=256,framerate=15/2",  true),
        ("Lepton","video/x-raw,format=GRAY16_LE,width=160,height=120,framerate=9/1",   true),
        ("Seek",  "video/x-raw,format=GRAY16_LE,width=206,height=156,framerate=15/2",  true),
    };

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

        // Gst.Application.Init() must be called ONCE per process — do it here,
        // before any GStreamer objects are created, and never again.
        // (WebcamWorker.RunPipelineAsync must NOT call it.)
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
        catch (System.Threading.Tasks.TaskCanceledException) { }
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

            // Stop all webcam workers in parallel, not sequentially
            var stopTasks = _devices.Keys
                .Select(id => _webcamManager.StopWebcamAsync(id))
                .ToArray();
            await SysTask.WhenAll(stopTasks);
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

        // Pre-populate caps hint for known thermal/speciality cameras so they
        // skip probing entirely, whether they arrive on KS or MF.
        if (!_nameToCapsHint.ContainsKey(name))
        {
            foreach (var (substr, caps, isGray) in KnownThermalDevices)
            {
                if (name.Contains(substr, System.StringComparison.OrdinalIgnoreCase))
                {
                    _nameToCapsHint[name] = (caps, isGray);
                    _logger.LogInformation(
                        "Pre-populated thermal caps hint for {Name}: {Caps}", name, caps);
                    break;
                }
            }
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

        // MF device arrived — always prefer it over KS
        if (isMF)
        {
            // Serialize concurrent MF arrivals for the same camera name.
            // On Windows, GStreamer can fire multiple mfdeviceX events for the
            // same physical device.  Without serialisation the duplicate guard
            // below races and two workers end up owning the same MF session.
            var upgradeLock = _nameUpgradeLocks.GetOrAdd(name, _ => new SemaphoreSlim(1, 1));

            _ = SysTask.Run(async () =>
            {
                await upgradeLock.WaitAsync();
                try
                {
                    // Re-check inside the lock — another MF event may have already
                    // registered this camera while we were waiting.
                    if (_nameToId.TryGetValue(name, out var existingId))
                    {
                        bool existingIsMF = existingId.Contains("mfdevice",
                            System.StringComparison.OrdinalIgnoreCase);

                        if (existingIsMF)
                        {
                            _logger.LogDebug(
                                "MF already registered for {Name} ({ExistingId}) — ignoring duplicate {NewId}",
                                name, existingId, id);
                            return;
                        }

                        // KS is running — stop and fully dispose before starting MF.
                        _logger.LogInformation("MF arrived for {Name} — replacing KS worker", name);
                        _devicePorts.TryGetValue(existingId, out int existingPort);

                        // Wait for the KS worker to finish probing so _cachedCaps is
                        // populated before we read it.  The probe holds _probeLock; we
                        // poll until the worker has a cached result or times out.
                        string? ksHintCaps = null;
                        bool ksHintIsGray = false;
                        var hintDeadline = System.DateTime.UtcNow.AddSeconds(30);
                        while (System.DateTime.UtcNow < hintDeadline)
                        {
                            var (c, g) = _webcamManager.GetCachedCaps(existingId);
                            if (c != null) { ksHintCaps = c; ksHintIsGray = g; break; }
                            await SysTask.Delay(200);
                        }

                        if (ksHintCaps != null)
                        {
                            _nameToCapsHint[name] = (ksHintCaps, ksHintIsGray);
                            _logger.LogInformation(
                                "Stored KS caps hint for {Name}: {Caps}", name, ksHintCaps);
                        }
                        else
                        {
                            _logger.LogWarning(
                                "KS worker for {Name} has no cached caps after 30 s — MF upgrade skipped",
                                name);
                            // KS is streaming fine with no caps yet (unlikely) or failed.
                            // Leave KS running rather than replacing with a broken MF worker.
                            return;
                        }

                        // Never upgrade thermal/gray devices to MF.  The FLIR (and
                        // similar) MF source advertises generic webcam caps it cannot
                        // actually stream; the KS driver exposes the correct native caps.
                        // Detect this by checking whether the KS hint uses a gray or
                        // non-standard format, or a non-standard resolution (height ≠
                        // 240/360/480/600/720/1080) that indicates a speciality sensor.
                        if (IsThermalOrSpecialityCaps(ksHintCaps))
                        {
                            _logger.LogInformation(
                                "KS caps hint for {Name} indicates thermal/speciality device ({Caps}) — keeping KS, skipping MF upgrade",
                                name, ksHintCaps);
                            return;
                        }

                        // Remove KS tracking before stopping so no further events
                        // can race and re-register it.
                        _devices.TryRemove(existingId, out _);
                        _nameToId.TryRemove(name, out _);
                        _devicePorts.TryRemove(existingId, out _);

                        // Pre-register the MF id NOW (inside the lock, before the
                        // async stop) so any further MF arrival events for the same
                        // camera name see it and return early from the duplicate guard.
                        _devices[id] = device;
                        _nameToId[name] = id;
                        _devicePorts[id] = existingPort;

                        try
                        {
                            await _webcamManager.StopWebcamAsync(existingId);
                            await SysTask.Delay(1000);

                            // Pass KS caps as hint so MF worker skips probing
                            _nameToCapsHint.TryGetValue(name, out var hint);
                            await _webcamManager.StartWebcamAsync(
                                device, "127.0.0.1", existingPort,
                                hint.caps, hint.isGray);
                            _logger.LogInformation(
                                "Camera connected (MF upgrade): {Name} | ID: {Id} | Port: {Port}",
                                name, id, existingPort);
                        }
                        catch (System.Exception ex)
                        {
                            _logger.LogError(ex, "Failed during KS→MF upgrade for {Name}", name);
                            // Roll back registration so the device can be retried
                            _devices.TryRemove(id, out _);
                            _nameToId.TryRemove(name, out _);
                            _devicePorts.TryRemove(id, out _);
                        }
                        return;
                    }

                    // No existing entry — fresh MF registration (no KS running)
                    // Pass any pre-populated hint (thermal table or prior KS probe)
                    _nameToCapsHint.TryGetValue(name, out var freshHint);
                    RegisterAndStartOnPort(device, id, name,
                        Interlocked.Increment(ref _nextPort),
                        freshHint.caps, freshHint.isGray);
                }
                finally
                {
                    upgradeLock.Release();
                }
            });
            return;
        }

        // KS device arrived — wait to see if MF follows.
        // On Windows, GStreamer's MF provider enumerates asynchronously and
        // typically arrives 800–1500 ms after KS.  Use a 2 s window so we almost
        // never fall back to KS unnecessarily.
        if (isKS)
        {
            _ = SysTask.Run(async () =>
            {
                await SysTask.Delay(2000);

                // If MF has already registered for this name, skip KS entirely
                if (_nameToId.TryGetValue(name, out var laterId) &&
                    laterId.Contains("mfdevice", System.StringComparison.OrdinalIgnoreCase))
                {
                    _logger.LogDebug("MF arrived in time — skipping KS for {Name}", name);
                    return;
                }

                // Also skip if any id has been registered for this device path
                // (covers the case where MF registered under a different name key)
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

        // Neither KS nor MF
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
        // Pass any pre-populated hint (e.g. thermal device table or prior KS probe)
        _nameToCapsHint.TryGetValue(name, out var hint);
        RegisterAndStartOnPort(device, id, name, port, hint.caps, hint.isGray);
    }

    private void RegisterAndStartOnPort(Device device, string id, string name, int port,
                                        string? capsHint = null, bool capsHintIsGray = false)
    {
        _devices[id] = device;
        _nameToId[name] = id;
        _devicePorts[id] = port;

        _logger.LogInformation("Camera connected: {Name} | ID: {Id} | Port: {Port}",
            name, id, port);

        _ = SysTask.Run(async () =>
        {
            try
            {
                await _webcamManager.StartWebcamAsync(device, "127.0.0.1", port,
                    capsHint, capsHintIsGray);
            }
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

    /// <summary>
    /// Returns true when the caps string indicates a thermal, infrared, or
    /// speciality sensor that should stay on KS rather than be upgraded to MF.
    /// Detection criteria:
    ///   - Gray/thermal pixel format (GRAY16_LE, GRAY8, GRAY16_BE)
    ///   - Non-standard sensor resolution height (512, 256, 288 — FLIR, Lepton, etc.)
    ///   - Sub-10 fps fractional framerate (e.g. 15/2 = 7.5 fps)
    /// </summary>
    private static bool IsThermalOrSpecialityCaps(string caps)
    {
        if (caps.Contains("GRAY16_LE") || caps.Contains("GRAY16_BE") || caps.Contains("GRAY8"))
            return true;
        if (caps.Contains(",height=512") || caps.Contains(",height=256") || caps.Contains(",height=288"))
            return true;
        if (caps.Contains("framerate=15/2") || caps.Contains("framerate=9/1"))
            return true;
        return false;
    }

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
}