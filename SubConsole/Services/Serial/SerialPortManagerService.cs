using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using SubConsole.Helpers;
using SubConsole.Models;
using SubConsole.Services.Serial.Commands;
using System.Collections.Concurrent;
using System.Threading.Channels;
using static SubConsole.Models.UsbDeviceInfo;

namespace SubConsole.Services.Serial;

// ═════════════════════════════════════════════════════════════════════════════
// Public surface
// ═════════════════════════════════════════════════════════════════════════════

//public enum SerialWorkerType { Text, Flir }

/// <summary>
/// All operations the command objects may invoke on the manager.
/// Keeping this as a separate interface makes the service testable without
/// requiring a real <see cref="SerialPortManagerService"/> instance.
/// </summary>
public interface ISerialPortManagerService
{
    // ── USB enumeration ───────────────────────────────────────────────────────
    Task<IReadOnlyList<UsbSerialPortInfo>> EnumerateUsbDevicesAsync(
        CancellationToken token = default);

    // ── Registry ──────────────────────────────────────────────────────────────
    //   void RegisterDevice(DeviceIdentifier identifier, string functionName, int baudRate, SerialWorkerType type);
    void RegisterDevice(UsbSerialPortInfo identifier, string functionName, int baudRate, SerialWorkerType type);

    Task<OperationResult> UnregisterDeviceAsync(string deviceKey, CancellationToken token = default);
    IReadOnlyList<DeviceRegistration> GetRegisteredDevices();

    // ── Port lifecycle ────────────────────────────────────────────────────────
    Task<OperationResult> OpenPortAsync(
        string deviceKey,CancellationToken token = default);
    Task<OperationResult> ClosePortAsync(string deviceKey, CancellationToken token = default);

    // ── I/O ───────────────────────────────────────────────────────────────────
    Task<OperationResult> WriteAsync(string functionName, byte[] data, CancellationToken token = default);
    Task<OperationResult> WriteTextAsync(string functionName, string text, CancellationToken token = default);

    ChannelReader<SerialMessage> GetMessageReader(IEnumerable<string> functionNames);

    // ── Auto-discovery ────────────────────────────────────────────────────────
    Task<OperationResultWithValue<int>> AutoDiscoverAsync(
        bool autoOpen, int defaultBaudRate, SerialWorkerType defaultType, CancellationToken token = default);

    // ── Command dispatcher ────────────────────────────────────────────────────
    Task<OperationResult> ExecuteAsync(ISerialCommand command, CancellationToken token = default);
}

// ═════════════════════════════════════════════════════════════════════════════
// Implementation
// ═════════════════════════════════════════════════════════════════════════════

public sealed class SerialPortManagerService : BackgroundService, ISerialPortManagerService
{
    private readonly ILogger<SerialPortManagerService> _logger;
    private readonly IDeviceRegistry _registry;
    //   private readonly IUsbDeviceEnumerator _enumerator;
    private readonly ISerialWorkerFactory _workerFactory;

    // function → worker
    private readonly ConcurrentDictionary<string, ISerialWorker> _workers = new();

    // Broadcast channel — all received messages from all ports flow here.
    // Filtered views are handed to consumers via GetMessageReader().
    private readonly Channel<SerialMessage> _broadcastChannel =
        Channel.CreateUnbounded<SerialMessage>(new UnboundedChannelOptions
        { SingleWriter = false, SingleReader = false });

    private CancellationToken _appToken;

    public SerialPortManagerService(
        ILogger<SerialPortManagerService> logger,
        IDeviceRegistry registry,
        IUsbDeviceEnumerator enumerator,
        ISerialWorkerFactory workerFactory)
    {
        _logger        = logger;
        _registry      = registry;
      //  _enumerator    = enumerator;
        _workerFactory = workerFactory;
    }

    // ── BackgroundService entry point ─────────────────────────────────────────

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _appToken = stoppingToken;
        _logger.LogInformation("SerialPortManagerService started");

        OperationResult result = await _registry.LoadDeviceRegistryFromDatabase();

        if (!result.IsSuccess)
        {
            _logger.LogError("Load from database failed");
        }

        // Fan-in task: reads from every worker's channel and writes to the broadcast.
        _ = Task.Run(() => BroadcastFanInAsync(stoppingToken), stoppingToken);

        try { await Task.Delay(Timeout.Infinite, stoppingToken); }
        catch (OperationCanceledException) { }
    }

    // ── USB enumeration ───────────────────────────────────────────────────────

    public async Task<IReadOnlyList<UsbSerialPortInfo>>
    EnumerateUsbDevicesAsync(CancellationToken token = default)
    => await UsbSerialPortMapper.GetUsbSerialPortsAsync(token);


    // ── Registry ──────────────────────────────────────────────────────────────

    //public void RegisterDevice(DeviceIdentifier identifier, string functionName, int baudRate, SerialWorkerType type)
    //    => _registry.Register(identifier, functionName, baudRate, type);

    public void RegisterDevice(UsbSerialPortInfo identifier, string functionName, int baudRate, SerialWorkerType type)
    => _registry.Register(identifier, functionName, baudRate, type);

    //public async Task<OperationResult> UnregisterDeviceAsync(
    //    string deviceKey, CancellationToken token = default)
    //{
    //    var close = await ClosePortAsync(deviceKey, token);
    // //   if (!close.IsSuccess) return close;

    //    var reg = _registry.AllRegistrations.FirstOrDefault(r => r.Identifier.Key == deviceKey);
    //    if (reg is not null)
    //    {
    //        _registry.Unregister(reg.Identifier);
    //        return OperationResult.Success();
    //    }
    //    else
    //    {
    //        return OperationResult.Failure($"Key: {deviceKey} not found in registered device list");
    //    }
    //}

    public async Task<OperationResult> UnregisterDeviceAsync(
    string function, CancellationToken token = default)
    {
        var close = await ClosePortAsync(function, token);
        //   if (!close.IsSuccess) return close;

        var reg = _registry.AllRegistrations.FirstOrDefault(r => r.FunctionName == function);
        if (reg is not null)
        {
            _registry.Unregister(reg.Identifier);
            return OperationResult.Success();
        }
        else
        {
            return OperationResult.Failure($"Function: {function} not found in registered device list");
        }
    }

    public IReadOnlyList<DeviceRegistration> GetRegisteredDevices()
        => _registry.AllRegistrations;

    // ── Port lifecycle ────────────────────────────────────────────────────────

    //public async Task<OperationResult> OpenPortAsync(
    //    string deviceKey, CancellationToken token = default)
    //{
    //    if (_workers.ContainsKey(deviceKey))
    //        return OperationResult.Failure($"Port for device '{deviceKey}' is already open");

    //    var reg = _registry.AllRegistrations.FirstOrDefault(r => r.Identifier.Key == deviceKey);
    //    if (reg is null)
    //        return OperationResult.Failure($"Device '{deviceKey}' is not registered");

    //    if (reg.CurrentPortPath is null)
    //    {
    //        // Attempt to find the device on the OS right now
    //        var refreshResult = await RefreshPortPathAsync(deviceKey, token);
    //        if (!refreshResult)
    //            return OperationResult.Failure(
    //                $"No OS port path found for device '{deviceKey}'");
    //    }

    //    SerialWorkerType workerType = reg.SerialWorkerType;

    //    int baudRate = reg.BaudRate;

    //    var portPath = reg.CurrentPortPath!;

    //    try
    //    {
    //        var worker = _workerFactory.Create(portPath, baudRate, workerType, _registry);

    //        if (!_workers.TryAdd(deviceKey, worker))
    //            return OperationResult.Failure($"Concurrent open for '{deviceKey}'");

    //        await worker.StartAsync(CancellationTokenSource
    //            .CreateLinkedTokenSource(_appToken, token).Token);

    //        _logger.LogInformation(
    //            "Opened port {Port} for device {Key} as {Type}",
    //            portPath, deviceKey, workerType);

    //        return OperationResult.Success();
    //    }
    //    catch (Exception ex)
    //    {
    //        _workers.TryRemove(deviceKey, out _);
    //        _logger.LogError(ex, "Failed to open port {Port}", portPath);
    //        return OperationResult.Failure(ex.Message);
    //    }
    //}

    public async Task<OperationResult> OpenPortAsync(
    string function, CancellationToken token = default)
    {
        if (_workers.ContainsKey(function))
            return OperationResult.Failure($"Port for function '{function}' is already open");

        var reg = _registry.AllRegistrations.FirstOrDefault(r => r.FunctionName == function);
        if (reg is null)
            return OperationResult.Failure($"Function '{function}' is not registered");

        if (reg.CurrentPortPath is null)
        {
            // Attempt to find the device on the OS right now
            var refreshResult = await RefreshPortPathAsync(function, token);
            if (!refreshResult)
                return OperationResult.Failure(
                    $"No OS port not found or has changed for function '{function}'");
        }

        SerialWorkerType workerType = reg.SerialWorkerType;

        int baudRate = reg.BaudRate;

        var portPath = reg.Identifier.PortName;

        try
        {
            var worker = _workerFactory.Create(portPath, baudRate, workerType, _registry);

            if (!_workers.TryAdd(function, worker))
                return OperationResult.Failure($"Concurrent open for '{function}'");

            var result = await worker.StartAsync(CancellationTokenSource
                .CreateLinkedTokenSource(_appToken, token).Token);

            if (result.IsSuccess)
            {
                _logger.LogInformation(
                 "Opened port {Port} for fucntion {Function} as {Type}",
                  portPath, function, workerType);

            }

            return result;

        }
        catch (Exception ex)
        {
            _workers.TryRemove(function, out _);
            _logger.LogError(ex, "Failed to open port {Port}", portPath);
            return OperationResult.Failure(ex.Message);
        }
    }



    //public async Task<OperationResult> ClosePortAsync(
    //    string deviceKey, CancellationToken token = default)
    //{
    //    if (!_workers.TryRemove(deviceKey, out var worker))
    //        return OperationResult.Failure($"No open port for device '{deviceKey}'");

    //    try
    //    {
    //        await worker.StopAsync();
    //        await worker.DisposeAsync();
    //        _registry.ClearPortPath(deviceKey);

    //        _logger.LogInformation("Closed port for device {Key}", deviceKey);
    //        return OperationResult.Success();
    //    }
    //    catch (Exception ex)
    //    {
    //        _logger.LogError(ex, "Error closing port for device {Key}", deviceKey);
    //        return OperationResult.Failure(ex.Message);
    //    }
    //}

    public async Task<OperationResult> ClosePortAsync(
    string function, CancellationToken token = default)
    {
        if (!_workers.TryRemove(function, out var worker))
            return OperationResult.Failure($"No open port for function '{function}'");

        try
        {
            await worker.StopAsync();
            await worker.DisposeAsync();

            string key = _registry.ResolveKeyForFucntion(function);
            if (key is null)
                return OperationResult.Failure($"No key found for function '{function}'");
            
            _registry.ClearPortPath(key);

            _logger.LogInformation("Closed port for function {function}", function);
            return OperationResult.Success();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error closing port for function {function}", function);
            return OperationResult.Failure(ex.Message);
        }
    }


    // ── I/O ───────────────────────────────────────────────────────────────────

    public async Task<OperationResult> WriteAsync(
        string functionName, byte[] data, CancellationToken token = default)
    {
        var portPath = _registry.ResolvePortPath(functionName);
        if (portPath is null)
            return OperationResult.Failure(
                $"Function '{functionName}' is not mapped to any device");

        // Reverse-look up the device key from the port path
       // var deviceKey = FindDeviceKeyByPort(portPath);
        if (functionName is null || !_workers.TryGetValue(functionName, out var worker))
            return OperationResult.Failure(
                $"Port for function '{functionName}' is not open");

        var result = await worker.WriteAsync(data, token);
        return result.IsSuccess
            ? OperationResult.Success()
            : OperationResult.Failure("Write channel full or closed");
    }

    public async Task<OperationResult> WriteTextAsync(
        string functionName, string text, CancellationToken token = default)
    {
        var portPath = _registry.ResolvePortPath(functionName);
        if (portPath is null)
            return OperationResult.Failure(
                $"Function '{functionName}' is not mapped to any device");

      //  var deviceKey = FindDeviceKeyByPort(portPath);
        if (functionName is null || !_workers.TryGetValue(functionName, out var worker))
            return OperationResult.Failure(
                $"Port for function '{functionName}' is not open");

        var result = await worker.WriteTextAsync(text, token);
        return result.IsSuccess
            ? OperationResult.Success()
            : OperationResult.Failure("Write channel full or closed");
    }






    /// <summary>
    /// Returns a <see cref="ChannelReader{T}"/> that yields only messages whose
    /// <see cref="SerialMessage.FunctionName"/> is in <paramref name="functionNames"/>.
    /// Each call creates a dedicated filtered channel — the caller owns it.
    /// </summary>
    public ChannelReader<SerialMessage> GetMessageReader(IEnumerable<string> functionNames)
    {
        var filter = functionNames.ToHashSet(StringComparer.OrdinalIgnoreCase);

        var filtered = Channel.CreateUnbounded<SerialMessage>(new UnboundedChannelOptions
        { SingleWriter = true, SingleReader = false });

        // Subscribe this filtered channel to the broadcast
        _ = Task.Run(async () =>
        {
            await foreach (var msg in _broadcastChannel.Reader.ReadAllAsync())
            {
                if (filter.Contains(msg.FunctionName))
                    await filtered.Writer.WriteAsync(msg);
            }
            filtered.Writer.TryComplete();
        });

        return filtered.Reader;
    }

    // ── Auto-discovery ────────────────────────────────────────────────────────

    public async Task<OperationResultWithValue<int>> AutoDiscoverAsync(
        bool autoOpen,
        int defaultBaudRate,
        SerialWorkerType defaultType,
        CancellationToken token = default)
    {
        //var devices = await _enumerator.EnumerateAsync(token);
        var devices = await UsbSerialPortMapper.GetUsbSerialPortsAsync(token);


        int opened = 0;

        foreach (UsbSerialPortInfo identifier in devices)
        {
            // Update the registry with current OS port paths for known devices
            var existing = _registry.AllRegistrations
                .FirstOrDefault(r => r.Identifier.Key == identifier.Key);

            if (existing is null)
            {
                _logger.LogDebug(
                    "AutoDiscover: unregistered device {Key} found on {Port}",
                    identifier.Key, identifier.PortName);
                continue;
            }

            _registry.SetPortPath(identifier.Key, identifier.PortName);

            if (!autoOpen || _workers.ContainsKey(identifier.Key)) continue;

            var result = await OpenPortAsync(identifier.Key, token);
            if (result.IsSuccess) opened++;
        }

        return OperationResultWithValue<int>.Success(opened);
    }

    // ── Command dispatcher ────────────────────────────────────────────────────

    public Task<OperationResult> ExecuteAsync(ISerialCommand command, CancellationToken token = default)
        => command.ExecuteAsync(this, token);

    // ── Shutdown ──────────────────────────────────────────────────────────────

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("Stopping SerialPortManagerService");

        var tasks = _workers.Values.Select(async w =>
        {
            await w.StopAsync();
            await w.DisposeAsync();
        });

        await Task.WhenAll(tasks);
        _workers.Clear();
        _broadcastChannel.Writer.TryComplete();

        await base.StopAsync(cancellationToken);
    }

    // ── Internal helpers ──────────────────────────────────────────────────────

    /// <summary>
    /// Fan-in loop: continuously reads from all workers and re-publishes
    /// to the shared broadcast channel. New workers added at runtime are
    /// picked up because each worker's reader is consumed independently.
    /// </summary>
    private async Task BroadcastFanInAsync(CancellationToken token)
    {
        // We snapshot and continuously scan active workers.
        // A dedicated task per worker is spawned when new workers appear.
        var spawned = new ConcurrentDictionary<string, bool>();

        while (!token.IsCancellationRequested)
        {
            foreach (var (key, worker) in _workers)
            {
                if (spawned.TryAdd(key, true))
                {
                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            await foreach (var msg in worker.ReceivedMessages.ReadAllAsync(token))
                            {
                                await _broadcastChannel.Writer.WriteAsync(msg, token);
                            }
                        }
                        catch (OperationCanceledException) { }
                        finally { spawned.TryRemove(key, out _); }
                    }, token);
                }
            }

            await Task.Delay(500, token).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        }
    }

    private string? FindDeviceKeyByPort(string portPath)
    {
        return _registry.AllRegistrations
            .FirstOrDefault(r =>
                string.Equals(r.CurrentPortPath, portPath, StringComparison.OrdinalIgnoreCase))
            ?.Identifier.Key;
    }

    //private async Task<bool> RefreshPortPathAsync(string deviceKey, CancellationToken token)
    //{
    //    var reg = _registry.AllRegistrations.FirstOrDefault(r => r.Identifier.Key == deviceKey);
    //    if (reg is null) return false;

    //    var devices = await UsbSerialPortMapper.GetUsbSerialPortsAsync(token);
    //    var match = devices.FirstOrDefault(d => d.Key == deviceKey);

    //    if (match!.PortName is null) return false;

    //    _registry.SetPortPath(deviceKey, match.PortName);
    //    return true;
    //}

    private async Task<bool> RefreshPortPathAsync(string function, CancellationToken token)
    {
        var reg = _registry.AllRegistrations.FirstOrDefault(r => r.FunctionName == function);
        if (reg is null) return false;

        var devices = await UsbSerialPortMapper.GetUsbSerialPortsAsync(token);
        var match = devices.FirstOrDefault(d => d.Key == reg.Key);

        if (match!.PortName is null) return false;

        _registry.SetPortPath(reg.Key, match.PortName);
        return true;
    }


}
