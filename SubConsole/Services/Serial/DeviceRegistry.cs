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
    Task<OperationResult> Register(
   UsbSerialPortInfo identifier, string functionName, int baudRate,
   SerialWorkerType serialWorker, SerialPortSettings? portSettings = null);

    DeviceRegistration? GetRegistration(string functionName);
    /// <summary>
    /// Register a device and associate it with a function name.
    /// Call this at startup or when a USB hotplug event is detected.
    /// </summary>
  //  void Register(DeviceIdentifier identifier, string functionName, int baudRate, SerialWorkerType serialWorker);

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
    string? ResolveKeyForFunction(string functionName);

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
        _logger.LogInformation(
            "Loading device registry from database");
        OperationResultWithValue<List<DeviceRegistration>> result = await _database.GetDeviceRegistriesAsync();
        if(!result.IsSuccess)
        {
            _logger.LogError(
                "Error loading device registry: {Success}. Reason: {Message}",
                false,
                result.Message);
            return OperationResult.Failure(result.Message);
        }

        foreach (var device in result.Value)
        {
            await Register(device.Identifier, device.FunctionName, device.BaudRate, device.SerialWorkerType, device.PortSettings);
        }
        _logger.LogInformation(
            "Completed loading device registry: {Success}. Devices loaded: {Count}",
            true,
            result.Value.Count);
        return OperationResult.Success();
    }
    // ── Registration ──────────────────────────────────────────────────────────

    //    public void Register(DeviceIdentifier identifier, string functionName, int baudRate, SerialWorkerType serialWorker)
    public async Task<OperationResult> Register(
        UsbSerialPortInfo identifier, string functionName, int baudRate,
        SerialWorkerType serialWorker, SerialPortSettings? portSettings = null)
    {
        _logger.LogInformation(
            "Registering device {Key} as function {Function} @ {Baud}",
            identifier.Key, functionName, baudRate);

        var reg = new DeviceRegistration(identifier, functionName, baudRate, serialWorker)
        {
            PortSettings = portSettings
        };

        _byKey[identifier.Key] = reg;
        _byFunction[functionName] = identifier.Key;

        OperationResult result = await _database.UpsertDeviceRegistrationAsync(reg);
        if (!result.IsSuccess)
        {
            _logger.LogError(
                "Error saving to database register device {Key} ({Function}): {Success}. Reason: {Message}",
                identifier.Key, functionName, false, result.Message);
            return OperationResult.Failure(result.Message);
        }

        _logger.LogInformation(
            "Completed register device {Key} ({Function}): {Success}",
            identifier.Key, functionName, true);
        return OperationResult.Success();
    }

    public DeviceRegistration? GetRegistration(string functionName)
    {
        if (!_byFunction.TryGetValue(functionName, out var key)) return null;
        return _byKey.TryGetValue(key, out var reg) ? reg : null;
    }

    //   public void Unregister(DeviceIdentifier identifier)
    public async Task<OperationResult> Unregister(UsbSerialPortInfo identifier)
    {
        _logger.LogInformation(
            "Unregistering device {Key}",
            identifier.Key);
        if (!_byKey.TryRemove(identifier.Key, out var reg))
        {
            _logger.LogWarning(
                "Error unregistering device {Key}: {Success}. Reason: Not found",
                identifier.Key,
                false);
            return OperationResult.Failure("Failed to remove identifier from Key dictionary");
        }

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
            _logger.LogError(
                "Error saving to database unregister device {Key}: {Success}. Reason: {Message}",
                identifier.Key,
                false,
                result.Message);
            return result;
        }
        _logger.LogInformation(
            "Completed unregister device {Key}: {Success}",
            identifier.Key,
            true);
        return OperationResult.Success();
    }

    // ── Port path management ──────────────────────────────────────────────────

    public void SetPortPath(string deviceKey, string portPath)
    {
        if (!_byKey.TryGetValue(deviceKey, out var reg))
        {
            _logger.LogWarning(
                "SetPortPath failed for {Key}: {Success}. Reason: Not registered",
                deviceKey,
                false);
            return;
        }

        reg.CurrentPortPath = portPath;
        _byPort[portPath] = deviceKey;

        _logger.LogInformation(
            "Completed port assignment {Key} → {Port}: {Success}",
            deviceKey,
            portPath,
            true);
    }

    public void ClearPortPath(string deviceKey)
    {

        if (!_byKey.TryGetValue(deviceKey, out var reg))
        {
            _logger.LogDebug(
                "ClearPortPath skipped for {Key}: no active port",
                deviceKey);
            return;
        }

        if (reg.CurrentPortPath is { } port)
        {
            _byPort.TryRemove(port, out _);
            reg.CurrentPortPath = null;
            _logger.LogInformation(
                "Completed clearing port for {Key}: {Success}",
                deviceKey,
                true);
        }
    }

    // ── Lookups ───────────────────────────────────────────────────────────────

    public string? ResolvePortPath(string functionName)
    {
        if (!_byFunction.TryGetValue(functionName, out var key))
        {
            _logger.LogDebug(
                "ResolvePortPath failed for {Function}: not found",
                functionName);
            return null;
        }

        if (!_byKey.TryGetValue(key, out var reg))
        {
            _logger.LogDebug(
                "ResolvePortPath failed for {Key}: not found",
                key);
            return null;
        }

        return reg.Identifier.PortName;
        //return reg.CurrentPortPath;
    }

    public string? ResolveKeyForFunction(string functionName)
    {
        if (!_byFunction.TryGetValue(functionName, out var key))
        {
            _logger.LogDebug(
                "ResolveKeyForFunction failed for {Function}",
                functionName);
            return null;
        }

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
        {
            _logger.LogDebug(
                "GetFunctionName: no mapping for port {Port}",
                portPath);
            return String.Empty;
        }

        if (!_byKey.TryGetValue(key, out var reg))
        {
            _logger.LogDebug(
                "GetFunctionName: no mapping for key {Key}",
                key);
            return String.Empty;
        }
        return reg.FunctionName;
    }

    public IReadOnlyList<DeviceRegistration> AllRegistrations =>
        _byKey.Values.ToList().AsReadOnly();
}
