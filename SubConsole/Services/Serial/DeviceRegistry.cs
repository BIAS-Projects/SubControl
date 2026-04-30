using Microsoft.Extensions.Logging;
using SubConsole.Models;
using SubConsole.Services.SQL;
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
    /// Register a device and associate it with a function name.
    /// Call this at startup or when a USB hotplug event is detected.
    /// </summary>
  //  void Register(DeviceIdentifier identifier, string functionName, int baudRate, SerialWorkerType serialWorker);
    Task<OperationResult> Register(UsbSerialPortInfo identifier, string functionName, int baudRate, SerialWorkerType serialWorker);
    /// <summary>Remove a device registration (e.g. on USB unplug).</summary>
  //  void Unregister(DeviceIdentifier identifier);
    Task<OperationResult> Unregister(UsbSerialPortInfo identifier);

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
    /// Resolve a function name to a USB device key.
    /// Returns null when the device is not registered or has no port assigned.
    /// </summary>
    string? ResolveKeyForFucntion(string functionName);

    /// <summary>
    /// Return the function names bound to the port that produced a message.
    /// Used when routing received data back to callers.
    /// </summary>
    string GetFunctionName(string portPath);

    /// <summary>
    /// Return the result of a database call.
    /// Used load the previously register devices saved t the database.
    /// </summary>
    Task<OperationResult> LoadDeviceRegistryFromDatabase();

    /// <summary>All currently registered devices.</summary>
    IReadOnlyList<DeviceRegistration> AllRegistrations { get; }
}

public sealed class DeviceRegistry : IDeviceRegistry
{
    private readonly ILogger<DeviceRegistry> _logger;

    private SQLiteService _database;

    // deviceKey → registration
    private readonly ConcurrentDictionary<string, DeviceRegistration> _byKey = new();

    // functionName → deviceKey  (one function name maps to exactly one device)
    private readonly ConcurrentDictionary<string, string> _byFunction = new(StringComparer.OrdinalIgnoreCase);
  //  private readonly ConcurrentDictionary<string, DeviceRegistration> _byFunction = new();

    // portPath → deviceKey  (updated when a port is opened)
    private readonly ConcurrentDictionary<string, string> _byPort = new(StringComparer.OrdinalIgnoreCase);

    public DeviceRegistry(ILogger<DeviceRegistry> logger, SQLiteService database)
    {
        _logger = logger;
        _database = database;
    }

    public async Task<OperationResult> LoadDeviceRegistryFromDatabase()
    {
        OperationResultWithValue<List<DeviceRegistration>> result = await _database.GetDeviceRegistriesAsync();
        if(!result.IsSuccess)
        {
            return OperationResult.Failure(result.Message);
        }

        foreach(var device in result.Value)
        {
            Register(device.Identifier, device.FunctionName, device.BaudRate, device.SerialWorkerType);
        }
        return OperationResult.Success();
    }
    // ── Registration ──────────────────────────────────────────────────────────

//    public void Register(DeviceIdentifier identifier, string functionName, int baudRate, SerialWorkerType serialWorker)
    public async Task<OperationResult> Register(UsbSerialPortInfo identifier, string functionName, int baudRate, SerialWorkerType serialWorker)
    {
       // var fns = functionNames.ToList();
        var reg = new DeviceRegistration(identifier, functionName, baudRate, serialWorker);

        _byKey[identifier.Key] = reg;

        _byFunction[functionName] = identifier.Key;

        OperationResult result = await _database.UpsertDeviceRegistrationAsync(reg);
        if(!result.IsSuccess)
        {
            return OperationResult.Failure(result.Message);
        }

        _logger.LogInformation("Registered function '{Function}' → device {Key}", functionName, identifier.Key);

        return OperationResult.Success();

    }

 //   public void Unregister(DeviceIdentifier identifier)
    public async Task<OperationResult> Unregister(UsbSerialPortInfo identifier)
    {
        if (!_byKey.TryRemove(identifier.Key, out var reg))
            return OperationResult.Failure("Failed to remove identifier from Key dictionary");

      //  foreach (var fn in reg.FunctionName)
            _byFunction.TryRemove(reg.FunctionName, out _);

        // Remove port mapping if present
        var stalePort = _byPort
            .FirstOrDefault(kv => kv.Value == identifier.Key).Key;

        if (stalePort is not null)
            _byPort.TryRemove(stalePort, out _);

        OperationResult result = await _database.DeleteDeviceRegistrationAsync(identifier.Key);

        if (!result.IsSuccess)
        {
            return result;
        }
        _logger.LogInformation("Unregistered device {Key}", identifier.Key);
        return OperationResult.Success();
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

        return reg.Identifier.PortName;
        //return reg.CurrentPortPath;
    }

    public string? ResolveKeyForFucntion(string functionName)
    {
        if (!_byFunction.TryGetValue(functionName, out var key))
            return null;

        return key;
        //return reg.CurrentPortPath;
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
