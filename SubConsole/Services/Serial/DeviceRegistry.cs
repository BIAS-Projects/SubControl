using Microsoft.Extensions.Logging;
using SubConsole.Models;
using System.Collections.Concurrent;
using static SubConsole.Models.UsbDeviceInfo;

namespace SubConsole.Services.Serial;

/// <summary>
/// Thread-safe store that maps:
///   DeviceIdentifier.Key  →  DeviceRegistration   (what we know about the device)
///   function name         →  DeviceIdentifier.Key  (reverse lookup)
/// </summary>
public interface IDeviceRegistry
{
    // ── Registration ─────────────────────────────────────────────────────────

    /// <summary>
    /// Register a device and associate it with one or more function names.
    /// Call this at startup or when a USB hotplug event is detected.
    /// </summary>
    void Register(DeviceIdentifier identifier, string functionName, int baudRate, SerialWorkerType serialWorker);

    /// <summary>Remove a device registration (e.g. on USB unplug).</summary>
    void Unregister(DeviceIdentifier identifier);

    // ── Port path management ──────────────────────────────────────────────────

    /// <summary>
    /// Record which OS port path a device is currently connected to.
    /// Called after the OS enumerator matches an identifier to a port.
    /// </summary>
    void SetPortPath(string deviceKey, string portPath);

    /// <summary>Clear the port path (device unplugged or port closed).</summary>
    void ClearPortPath(string deviceKey);

    // ── Lookups ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Resolve a function name to an OS port path.
    /// Returns null when the device is not registered or has no port assigned.
    /// </summary>
    string? ResolvePortPath(string functionName);

    /// <summary>
    /// Return the function names bound to the port that produced a message.
    /// Used when routing received data back to callers.
    /// </summary>
    string GetFunctionName(string portPath);

    /// <summary>All currently registered devices.</summary>
    IReadOnlyList<DeviceRegistration> AllRegistrations { get; }
}

public sealed class DeviceRegistry : IDeviceRegistry
{
    private readonly ILogger<DeviceRegistry> _logger;

    // deviceKey → registration
    private readonly ConcurrentDictionary<string, DeviceRegistration> _byKey = new();

    // functionName → deviceKey  (one function name maps to exactly one device)
    private readonly ConcurrentDictionary<string, string> _byFunction = new(StringComparer.OrdinalIgnoreCase);

    // portPath → deviceKey  (updated when a port is opened)
    private readonly ConcurrentDictionary<string, string> _byPort = new(StringComparer.OrdinalIgnoreCase);

    public DeviceRegistry(ILogger<DeviceRegistry> logger) => _logger = logger;

    // ── Registration ──────────────────────────────────────────────────────────

    public void Register(DeviceIdentifier identifier, string functionName, int baudRate, SerialWorkerType serialWorker)
    {
       // var fns = functionNames.ToList();
        var reg = new DeviceRegistration(identifier, functionName, baudRate, serialWorker);

        _byKey[identifier.Key] = reg;

        _logger.LogInformation("Registered function '{Function}' → device {Key}", functionName, identifier.Key);

    }

    public void Unregister(DeviceIdentifier identifier)
    {
        if (!_byKey.TryRemove(identifier.Key, out var reg))
            return;

      //  foreach (var fn in reg.FunctionName)
            _byFunction.TryRemove(reg.FunctionName, out _);

        // Remove port mapping if present
        var stalePort = _byPort
            .FirstOrDefault(kv => kv.Value == identifier.Key).Key;

        if (stalePort is not null)
            _byPort.TryRemove(stalePort, out _);

        _logger.LogInformation("Unregistered device {Key}", identifier.Key);
    }

    // ── Port path management ──────────────────────────────────────────────────

    public void SetPortPath(string deviceKey, string portPath)
    {
        if (!_byKey.TryGetValue(deviceKey, out var reg))
        {
            _logger.LogWarning(
                "SetPortPath: device key '{Key}' not found in registry", deviceKey);
            return;
        }

        reg.CurrentPortPath = portPath;
        _byPort[portPath] = deviceKey;

        _logger.LogInformation(
            "Device {Key} assigned to port {Port}", deviceKey, portPath);
    }

    public void ClearPortPath(string deviceKey)
    {
        if (!_byKey.TryGetValue(deviceKey, out var reg))
            return;

        if (reg.CurrentPortPath is { } port)
        {
            _byPort.TryRemove(port, out _);
            reg.CurrentPortPath = null;
            _logger.LogInformation(
                "Cleared port path for device {Key}", deviceKey);
        }
    }

    // ── Lookups ───────────────────────────────────────────────────────────────

    public string? ResolvePortPath(string functionName)
    {
        if (!_byFunction.TryGetValue(functionName, out var key))
            return null;

        if (!_byKey.TryGetValue(key, out var reg))
            return null;

        return reg.CurrentPortPath;
    }

    //public IReadOnlyList<string> GetFunctionNames(string portPath)
    //{
    //    if (!_byPort.TryGetValue(portPath, out var key))
    //        return Array.Empty<string>();

    //    return _byKey.TryGetValue(key, out var reg)
    //        ? reg.FunctionName
    //        : Array.Empty<string>();
    //}

    public string GetFunctionName(string portPath)
    {
        if (!_byPort.TryGetValue(portPath, out var key))
            return String.Empty;

        return _byKey.TryGetValue(key, out var reg)
            ? reg.FunctionName
            : String.Empty;
    }

    public IReadOnlyList<DeviceRegistration> AllRegistrations =>
        _byKey.Values.ToList().AsReadOnly();
}
