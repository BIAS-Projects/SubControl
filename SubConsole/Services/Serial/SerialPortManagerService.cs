using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using SubConsole.Models;
using SubConsole.Services.Helpers;
using SubConsole.Services.Serial.Commands;
using System.Collections.Concurrent;
using System.Threading.Channels;
using static SQLite.SQLite3;
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

    Task<PortRemapResult> HandlePortChangedAsync(PortChangedEventArgs e, CancellationToken token = default);
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
    //private readonly Channel<SerialMessage> _broadcastChannel =
    //    Channel.CreateUnbounded<SerialMessage>(new UnboundedChannelOptions
    //    { SingleWriter = false, SingleReader = false });
    private readonly Channel<SerialMessage> _broadcastChannel =
    Channel.CreateUnbounded<SerialMessage>(new UnboundedChannelOptions
    { SingleWriter = false, SingleReader = false });

    private CancellationToken _appToken;

    private readonly SemaphoreSlim _remapLock = new(1, 1);

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
        _logger.LogInformation(
            "Starting serial port manager service");

        OperationResult result = await _registry.LoadDeviceRegistryFromDatabase();

        if (!result.IsSuccess)
        {
            _logger.LogError(
                "Error loading device registry: {Success}. Reason: {Message}",
                false,
                result.Message);
        }

        _logger.LogInformation(
            "Completed loading device registry: {Success}",
            result.IsSuccess);


        // Fan-in task: reads from every worker's channel and writes to the broadcast.
        // _ = Task.Run(() => BroadcastFanInAsync(stoppingToken), stoppingToken);

        try { await Task.Delay(Timeout.Infinite, stoppingToken); }
        catch (OperationCanceledException) { }
    }

    // ── USB enumeration ───────────────────────────────────────────────────────

    public async Task<IReadOnlyList<UsbSerialPortInfo>>
    EnumerateUsbDevicesAsync(CancellationToken token = default)
    => await UsbSerialPortMapper.GetUsbSerialPortsAsync(token);


    // ── Registry ──────────────────────────────────────────────────────────────


    public void RegisterDevice(UsbSerialPortInfo identifier, string functionName, int baudRate, SerialWorkerType type)
    => _registry.Register(identifier, functionName, baudRate, type);

 

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



    public async Task<OperationResult> OpenPortAsync(
        string function, CancellationToken token = default)
    {
        _logger.LogInformation(
            "Opening port for function {Function}",
            function);

        var reg = _registry.AllRegistrations.FirstOrDefault(r => r.FunctionName == function);
        if (reg is null)
        {
            _logger.LogWarning(
                "Error in open port {Function}: {Success}. Reason: Not registered",
                function, false);
            return OperationResult.Failure($"Function '{function}' is not registered");
        }

        if (_workers.ContainsKey(function))
        {
            _logger.LogWarning(
                "Port for function {Function} is already open",
                function);
            return OperationResult.Success($"Port for function '{function}' is already open");
        }

        var conflictingFunction = _workers.Keys
            .FirstOrDefault(f =>
            {
                var r = _registry.AllRegistrations.FirstOrDefault(x => x.FunctionName == f);
                return r?.Identifier.PortName == reg.Identifier.PortName;
            });

        if (conflictingFunction is not null)
        {
            _logger.LogWarning(
                "Error in open port {Function}: {Success}. Reason: Port already open under {ConflictingFunction}",
                function, false, conflictingFunction);
            return OperationResult.Failure(
                $"Port for function '{function}' is already open under function '{conflictingFunction}'");
        }

        if (reg.CurrentPortPath is null)
        {
            var refreshResult = await RefreshPortPathAsync(function, token);
            if (!refreshResult)
            {
                _logger.LogWarning(
                    "Error in open port {Function}: {Success}. Reason: OS port not found or has changed for function",
                    function, false);
                return OperationResult.Failure(
                    $"OS port not found or has changed for function '{function}'");
            }
        }

        SerialWorkerType workerType = reg.SerialWorkerType;
        int baudRate = reg.BaudRate;
        var portPath = reg.Identifier.PortName;

        try
        {
            var worker = _workerFactory.Create(portPath, baudRate, workerType, _registry);
            if (!_workers.TryAdd(function, worker))
                return OperationResult.Failure($"Concurrent open for '{function}'");

            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(_appToken, token);
            var result = await worker.StartAsync(linkedCts.Token);

            if (result.IsSuccess)
            {
                _logger.LogDebug(
                    "Subscribing worker for {Function} - worker count now {Count}",
                    function, _workers.Count);
                SubscribeWorkerToBroadcast(function, worker, _appToken);
                _logger.LogDebug("Subscribed worker for {Function}", function);
                _logger.LogInformation(
                    "Completed open port {Function} on {Port} as {Type}: {Success}",
                    function, portPath, workerType, true);
            }
            else
            {
                _logger.LogWarning(
                    "Error in open port {Function}: {Success}. Reason: {Message}",
                    function, false, result.Message);
                _workers.TryRemove(function, out _);
            }

            return result;
        }
        catch (Exception ex)
        {
            _workers.TryRemove(function, out _);
            _logger.LogError(
                ex,
                "Completed open port {Function}: {Success}",
                function, false);
            return OperationResult.Failure(ex.Message);
        }
    }


    private void SubscribeWorkerToBroadcast(string function, ISerialWorker worker, CancellationToken token)
    {


        _ = Task.Run(async () =>
        {
            try
            {
                // Wait for the worker to signal it is ready — should be near-instant
                // since we call this after StartAsync succeeds, but the TCS guards
                // against any timing gap between TryAdd and the reader loop starting.
                await worker.Started.WaitAsync(token);
            }
            catch (OperationCanceledException) { return; }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Worker {Function} failed before fan-in could subscribe",
                    function);
                return;
            }

            _logger.LogDebug(
                "Subscribed worker {Function} to broadcast",
                function);

            try
            {
                await foreach (var msg in worker.ReceivedMessages.ReadAllAsync(token))
                {
                    _logger.LogDebug("Fan-in writing to broadcast: {Function} {Text}",
                    msg.FunctionName, msg.Text);

                    await _broadcastChannel.Writer.WriteAsync(msg, token);
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                _logger.LogWarning(
                    ex,
                    "Fan-in failure for {Function}",
                    function);
            }

            // Channel completed (port dropped or worker stopped) — nothing to clean up here.
            // The worker itself is already removed from _workers by ClosePortAsync or the
            // port-change handler. If it was an unexpected drop you may want to raise an event.
            _logger.LogDebug(
                "Unsubscribed worker {Function} from broadcast",
                function);

        }, token);
    }

    public async Task<OperationResult> ClosePortAsync(
    string function, CancellationToken token = default)
    {
        _logger.LogInformation(
            "Closing port for function {Function}",
            function);

        if (!_workers.TryRemove(function, out var worker))
        {
            _logger.LogWarning(
                "Error in close port {Function}: {Success}. Reason: Not open",
                function,
                false);
            return OperationResult.Failure($"No open port for function '{function}'");
        }

        try
        {
            await worker.StopAsync();
            await worker.DisposeAsync();

            string key = _registry.ResolveKeyForFunction(function);
            if (key is null)
            {
                _logger.LogWarning(
                    "Error in close port {Function}: {Success}. Reason: No key found for function",
                    function,
                    false);
                return OperationResult.Failure($"No key found for function '{function}'");
            }
            
            _registry.ClearPortPath(key);

            _logger.LogInformation(
                "Completed close port {Function}: {Success}",
                function,
                true);
            return OperationResult.Success();
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "Exception in close port {Function}: {Success}",
                function,
                false);
            return OperationResult.Failure(ex.Message);
        }
    }




    public async Task<PortRemapResult> HandlePortChangedAsync(
    PortChangedEventArgs e, CancellationToken token = default)
    {
        _logger.LogInformation(
            "Handling port change {Kind} for {Port}",
            e.Kind,
            e.Port.PortName);

        await _remapLock.WaitAsync(token);
        try
        {
            return e.Kind switch
            {
                PortChangeKind.Removed => await HandlePortRemovedAsync(e.Port, token),
                PortChangeKind.Added => await HandlePortAddedAsync(e.Port, token),
                _ => PortRemapResult.NoOp()
            };
        }
        finally { _remapLock.Release(); }
    }

    private async Task<PortRemapResult> HandlePortRemovedAsync(
        UsbSerialPortInfo port, CancellationToken token)
    {

        var reg = _registry.AllRegistrations
            .FirstOrDefault(r => r.Identifier.Key == port.Key);

        if (reg is null) return PortRemapResult.NoOp();

        string oldPort = reg.CurrentPortPath ?? port.PortName;

        await ClosePortAsync(reg.FunctionName, token);

        _logger.LogInformation(
            "Completed port removal {Function} from {OldPort}: {Success}",
            reg.FunctionName,
            oldPort,
            true);

        return new PortRemapResult(
            Function: reg.FunctionName,
            OldPort: oldPort,
            NewPort: null,
            Kind: PortChangeKind.Removed,
            Error: null);
    }

    private async Task<PortRemapResult> HandlePortAddedAsync(
        UsbSerialPortInfo port, CancellationToken token)
    {
        var reg = _registry.AllRegistrations
            .FirstOrDefault(r => r.Identifier.Key == port.Key);

        if (reg is null) return PortRemapResult.NoOp();

        // Could be a rename (COM3→COM4) or a reconnect on the same port
        string oldPort = reg.CurrentPortPath ?? "(none)";

        _registry.SetPortPath(port.Key, port.PortName);

        var result = await OpenPortAsync(reg.FunctionName, token);

        _logger.LogInformation(
            "Completed port add/remap {Function}: {OldPort} → {NewPort}: {Success}",
            reg.FunctionName,
            oldPort,
            port.PortName,
            result.IsSuccess);

        return new PortRemapResult(
            Function: reg.FunctionName,
            OldPort: oldPort,
            NewPort: port.PortName,
            Kind: PortChangeKind.Added,
            Error: result.IsSuccess ? null : result.Message);
    }

    // ── I/O ───────────────────────────────────────────────────────────────────

    //public async Task<OperationResult> WriteAsync(
    //    string functionName, byte[] data, CancellationToken token = default)
    //{
    //    var portPath = _registry.ResolvePortPath(functionName);
    //    if (portPath is null)
    //        return OperationResult.Failure(
    //            $"Function '{functionName}' is not mapped to any device");

    //    // Reverse-look up the device key from the port path
    //    if (functionName is null || !_workers.TryGetValue(functionName, out var worker))
    //        return OperationResult.Failure(
    //            $"Port for function '{functionName}' is not open");

    //    var result = await worker.WriteAsync(data, token);
    //    //return result.IsSuccess
    //    //    ? OperationResult.Success()
    //    //    : OperationResult.Failure("Write channel full or closed");
    //    return await worker.WriteAsync(data, token);
    //}

    //public async Task<OperationResult> WriteTextAsync(
    //    string functionName, string text, CancellationToken token = default)
    //{
    //    var portPath = _registry.ResolvePortPath(functionName);
    //    if (portPath is null)
    //        return OperationResult.Failure(
    //            $"Function '{functionName}' is not mapped to any device");

    //  //  var deviceKey = FindDeviceKeyByPort(portPath);
    //    if (functionName is null || !_workers.TryGetValue(functionName, out var worker))
    //        return OperationResult.Failure(
    //            $"Port for function '{functionName}' is not open");

    //    var result = await worker.WriteTextAsync(text, token);
    //    //return result.IsSuccess
    //    //    ? OperationResult.Success()
    //    //    : OperationResult.Failure("Write channel full or closed");
    //    return await worker.WriteTextAsync(text, token);
    //}

    public async Task<OperationResult> WriteAsync(
    string functionName, byte[] data, CancellationToken token = default)
    {
        _logger.LogDebug(
            "Writing to function {Function} ({ByteCount} bytes)",
            functionName,
            data.Length);

        if (_registry.ResolvePortPath(functionName) is null)
        {
            _logger.LogWarning(
                "Error in write {Function}: {Success}. Reason: Not mapped",
                functionName,
                false);
            return OperationResult.Failure($"Function '{functionName}' is not mapped to any device");
        }

        if (!_workers.TryGetValue(functionName, out var worker))
        {
            _logger.LogWarning(
                "Error in write {Function}: {Success}. Reason: Is not open",
                functionName,
                false);
            return OperationResult.Failure($"Port for function '{functionName}' is not open");

        }

        var result = await worker.WriteAsync(data, token);

        _logger.LogInformation(
            "Completed write {Function}: {Success}",
            functionName,
            result.IsSuccess);
        return result;
    }

    public async Task<OperationResult> WriteTextAsync(
    string functionName, string text, CancellationToken token = default)
    {
        if (_registry.ResolvePortPath(functionName) is null)
            return OperationResult.Failure($"Function '{functionName}' is not mapped to any device");

        if (!_workers.TryGetValue(functionName, out var worker))
            return OperationResult.Failure($"Port for function '{functionName}' is not open");

        return await worker.WriteTextAsync(text, token);
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

                _logger.LogDebug("GetMessageReader received: {Function} {Text}",
                msg.FunctionName, msg.Text);

                // Empty set = subscribe to ALL (wildcard), non-empty = filter
                if (filter.Count == 0 || filter.Contains(msg.FunctionName))
                    await filtered.Writer.WriteAsync(msg);
            }
            filtered.Writer.TryComplete();
        });

        _logger.LogDebug(
            "Created message reader for {FunctionCount} functions",
            filter.Count);

        return filtered.Reader;
    }

    // ── Auto-discovery ────────────────────────────────────────────────────────

    public async Task<OperationResultWithValue<int>> AutoDiscoverAsync(
        bool autoOpen,
        int defaultBaudRate,
        SerialWorkerType defaultType,
        CancellationToken token = default)
    {
        _logger.LogInformation(
            "Starting auto-discovery (AutoOpen={AutoOpen})",
            autoOpen);

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
                    "Auto-discovery found unregistered device {Key} on {Port}",
                    identifier.Key,
                    identifier.PortName);
                continue;
            }

            _registry.SetPortPath(identifier.Key, identifier.PortName);


            if (!autoOpen || _workers.ContainsKey(existing.FunctionName)) continue;
            var result = await OpenPortAsync(existing.FunctionName, token);

            if (result.IsSuccess) opened++;
        }

        _logger.LogInformation(
            "Completed auto-discovery: {OpenedCount} ports opened",
            opened);

        return OperationResultWithValue<int>.Success(opened);
    }

    // ── Command dispatcher ────────────────────────────────────────────────────

    public Task<OperationResult> ExecuteAsync(ISerialCommand command, CancellationToken token = default)
        => command.ExecuteAsync(this, token);

    // ── Shutdown ──────────────────────────────────────────────────────────────

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation(
            "Stopping serial port manager service");

        var tasks = _workers.Values.Select(async w =>
        {
            await w.StopAsync();
            await w.DisposeAsync();
        });

        await Task.WhenAll(tasks);
        _workers.Clear();
        _broadcastChannel.Writer.TryComplete();

        await base.StopAsync(cancellationToken);

        _logger.LogInformation(
            "Completed stopping serial port manager service: {Success}",
            true);
    }

    // ── Internal helpers ──────────────────────────────────────────────────────

    /// <summary>
    /// Fan-in loop: continuously reads from all workers and re-publishes
    /// to the shared broadcast channel. New workers added at runtime are
    /// picked up because each worker's reader is consumed independently.
    /// </summary>
    //private async Task BroadcastFanInAsync(CancellationToken token)
    //{
    //    // We snapshot and continuously scan active workers.
    //    // A dedicated task per worker is spawned when new workers appear.
    //    var spawned = new ConcurrentDictionary<string, bool>();

    //    while (!token.IsCancellationRequested)
    //    {
    //        foreach (var (key, worker) in _workers)
    //        {
    //            if (spawned.TryAdd(key, true))
    //            {
    //                _ = Task.Run(async () =>
    //                {
    //                    try
    //                    {
    //                        await foreach (var msg in worker.ReceivedMessages.ReadAllAsync(token))
    //                        {
    //                            await _broadcastChannel.Writer.WriteAsync(msg, token);
    //                        }
    //                    }
    //                    catch (OperationCanceledException) { }
    //                    finally { spawned.TryRemove(key, out _); }
    //                }, token);
    //            }
    //        }

    //        await Task.Delay(500, token).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
    //    }
    //}

    //private string? FindDeviceKeyByPort(string portPath)
    //{
    //    return _registry.AllRegistrations
    //        .FirstOrDefault(r =>
    //            string.Equals(r.CurrentPortPath, portPath, StringComparison.OrdinalIgnoreCase))
    //        ?.Identifier.Key;
    //}


    private async Task<bool> RefreshPortPathAsync(string function, CancellationToken token)
    {
        _logger.LogDebug("Refreshing port path for {Function}", function);

        var reg = _registry.AllRegistrations.FirstOrDefault(r => r.FunctionName == function);
        if (reg is null)
        {
            _logger.LogWarning(
                "Refresh port path {Function}: {Success}. Reason: Not registered",
                function, false);
            return false;
        }

        var devices = await UsbSerialPortMapper.GetUsbSerialPortsAsync(token);
        var match = devices.FirstOrDefault(d => d.Key == reg.Key);

        if (match is null || match.PortName is null)
        {
            _logger.LogWarning(
                "Refresh port path {Function}: {Success}. Reason: No matching device found for key {Key}",
                function, false, reg.Key);
            return false;
        }

        _registry.SetPortPath(reg.Key, match.PortName);

        _logger.LogInformation(
            "Completed port refresh {Function} resolved to {PortName}: {Success}",
            function, match.PortName, true);
        return true;
    }


}
