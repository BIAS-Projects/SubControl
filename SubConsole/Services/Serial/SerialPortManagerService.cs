
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using SubConsole.Models;
using SubConsole.Services.Helpers;
using SubConsole.Services.Serial.Commands;
using System.Collections.Concurrent;
using System.Text.Json;
using System.Threading.Channels;
using static SubConsole.Models.UsbDeviceInfo;

namespace SubConsole.Services.Serial;

// ═════════════════════════════════════════════════════════════════════════════
// Public surface
// ═════════════════════════════════════════════════════════════════════════════

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
    Task<OperationResult> OpenPortAsync(string deviceKey, CancellationToken token = default);
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
    // ── Reserved function name used for status messages on the broadcast channel
    private const string SystemFunction = "SYSTEM";

    // How often to scan for the device when polling for reconnect
    private static readonly TimeSpan ReconnectPollInterval = TimeSpan.FromSeconds(2);

    private readonly ILogger<SerialPortManagerService> _logger;
    private readonly IDeviceRegistry _registry;
    private readonly ISerialWorkerFactory _workerFactory;

    // function → worker
    private readonly ConcurrentDictionary<string, ISerialWorker> _workers = new();

    // Tracks which functions were open at the time of disconnection so we can
    // auto-reopen only those ports when the device comes back.
    // Also acts as a guard so only one poll loop runs per function at a time.
    private readonly ConcurrentDictionary<string, bool> _wasOpenBeforeDisconnect = new();

    // Broadcast channel — all received messages from all ports flow here.
    // Status messages (disconnect / reconnect) also flow here with FunctionName = "SYSTEM".
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
        _logger = logger;
        _registry = registry;
        _workerFactory = workerFactory;
    }

    // ── BackgroundService entry point ─────────────────────────────────────────

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _appToken = stoppingToken;
        _logger.LogInformation("Starting serial port manager service");

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

        try { await Task.Delay(Timeout.Infinite, stoppingToken); }
        catch (OperationCanceledException) { }
    }

    // ── USB enumeration ───────────────────────────────────────────────────────

    public async Task<IReadOnlyList<UsbSerialPortInfo>> EnumerateUsbDevicesAsync(
        CancellationToken token = default)
        => await UsbSerialPortMapper.GetUsbSerialPortsAsync(token);

    // ── Registry ──────────────────────────────────────────────────────────────

    public void RegisterDevice(
        UsbSerialPortInfo identifier, string functionName, int baudRate, SerialWorkerType type)
        => _registry.Register(identifier, functionName, baudRate, type);

    public async Task<OperationResult> UnregisterDeviceAsync(
        string function, CancellationToken token = default)
    {
        var close = await ClosePortAsync(function, token);

        var reg = _registry.AllRegistrations.FirstOrDefault(r => r.FunctionName == function);
        if (reg is not null)
        {
            _registry.Unregister(reg.Identifier);
            _wasOpenBeforeDisconnect.TryRemove(function, out _);
            return OperationResult.Success();
        }

        return OperationResult.Failure($"Function: {function} not found in registered device list");
    }

    public IReadOnlyList<DeviceRegistration> GetRegisteredDevices()
        => _registry.AllRegistrations;

    // ── Port lifecycle ────────────────────────────────────────────────────────

    public async Task<OperationResult> OpenPortAsync(
        string function, CancellationToken token = default)
    {
        _logger.LogInformation("Opening port for function {Function}", function);

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
            _logger.LogWarning("Port for function {Function} is already open", function);
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
                ex, "Completed open port {Function}: {Success}", function, false);
            return OperationResult.Failure(ex.Message);
        }
    }

    public async Task<OperationResult> ClosePortAsync(
        string function, CancellationToken token = default)
    {
        _logger.LogInformation("Closing port for function {Function}", function);

        if (!_workers.TryRemove(function, out var worker))
        {
            _logger.LogWarning(
                "Error in close port {Function}: {Success}. Reason: Not open",
                function, false);
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
                    function, false);
                return OperationResult.Failure($"No key found for function '{function}'");
            }

            _registry.ClearPortPath(key);

            _logger.LogInformation(
                "Completed close port {Function}: {Success}", function, true);
            return OperationResult.Success();
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex, "Exception in close port {Function}: {Success}", function, false);
            return OperationResult.Failure(ex.Message);
        }
    }

    // ── Port-change handling (driven by UsbPortRegistry events) ──────────────
    // NOTE: On Windows, UsbPortRegistry.ApplySnapshot cannot reliably detect
    // removal because Windows keeps the COM port in the registry after physical
    // unplug. The primary disconnect path is SubscribeWorkerToBroadcast detecting
    // the worker channel completing and starting PollForReconnectAsync.
    // HandlePortChangedAsync is retained as a secondary path for cases where the
    // registry diff does fire correctly (e.g. Linux, or port reassignment).

    public async Task<PortRemapResult> HandlePortChangedAsync(
        PortChangedEventArgs e, CancellationToken token = default)
    {
        _logger.LogInformation(
            "Handling port change {Kind} for {Port}",
            e.Kind, e.Port.PortName);

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

        // Only close and mark if not already handled by the polling path.
        bool wasOpen = _workers.ContainsKey(reg.FunctionName);
        if (wasOpen)
        {
            _wasOpenBeforeDisconnect[reg.FunctionName] = true;
            _logger.LogDebug(
                "Port {Function} was open at time of disconnect — marked for auto-reconnect",
                reg.FunctionName);
        }

        await ClosePortAsync(reg.FunctionName, token);

        await PublishStatusMessageAsync(
            reg.FunctionName,
            SerialMessageKind.Disconnected,
            $"Device disconnected from {oldPort}",
            token);

        _logger.LogInformation(
            "Completed port removal {Function} from {OldPort}: {Success}",
            reg.FunctionName, oldPort, true);

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

        string oldPort = reg.CurrentPortPath ?? "(none)";

        _registry.SetPortPath(port.Key, port.PortName);

        // If polling is already running for this function, let it handle
        // the reopen — it will find the device on its next scan.
        if (_wasOpenBeforeDisconnect.ContainsKey(reg.FunctionName))
        {
            _logger.LogInformation(
                "Port {Function} added event received — poll loop will handle reopen on {Port}",
                reg.FunctionName, port.PortName);

            return new PortRemapResult(
                Function: reg.FunctionName,
                OldPort: oldPort,
                NewPort: port.PortName,
                Kind: PortChangeKind.Added,
                Error: null);
        }

        // Port was not open before — just notify, don't auto-open.
        await PublishStatusMessageAsync(
            reg.FunctionName,
            SerialMessageKind.Reconnected,
            $"Device reconnected on {port.PortName} (port was closed — manual open required)",
            token);

        _logger.LogInformation(
            "Completed port add {Function}: {OldPort} → {NewPort} (was not open, skipping auto-open)",
            reg.FunctionName, oldPort, port.PortName);

        return new PortRemapResult(
            Function: reg.FunctionName,
            OldPort: oldPort,
            NewPort: port.PortName,
            Kind: PortChangeKind.Added,
            Error: null);
    }

    // ── I/O ───────────────────────────────────────────────────────────────────

    public async Task<OperationResult> WriteAsync(
        string functionName, byte[] data, CancellationToken token = default)
    {
        _logger.LogDebug(
            "Writing to function {Function} ({ByteCount} bytes)",
            functionName, data.Length);

        var portCheckResult = CheckPortAvailable(functionName);
        if (portCheckResult is not null)
        {
            _logger.LogWarning(
                "Error in write {Function}: {Success}. Reason: {Message}",
                functionName, false, portCheckResult.Message);
            return portCheckResult;
        }

        if (!_workers.TryGetValue(functionName, out var worker))
        {
            _logger.LogWarning(
                "Error in write {Function}: {Success}. Reason: Is not open",
                functionName, false);
            return OperationResult.Failure($"Port for function '{functionName}' is not open");
        }

        var result = await worker.WriteAsync(data, token);

        _logger.LogInformation(
            "Completed write {Function}: {Success}", functionName, result.IsSuccess);
        return result;
    }

    public async Task<OperationResult> WriteTextAsync(
        string functionName, string text, CancellationToken token = default)
    {
        var portCheckResult = CheckPortAvailable(functionName);
        if (portCheckResult is not null)
        {
            _logger.LogWarning(
                "Error in write text {Function}: {Success}. Reason: {Message}",
                functionName, false, portCheckResult.Message);
            return portCheckResult;
        }

        if (!_workers.TryGetValue(functionName, out var worker))
        {
            _logger.LogWarning(
                "Error in write text {Function}: {Success}. Reason: Is not open",
                functionName, false);
            return OperationResult.Failure($"Port for function '{functionName}' is not open");
        }

        return await worker.WriteTextAsync(text, token);
    }

    /// <summary>
    /// Checks whether a function's port is registered and not currently
    /// disconnected.  Returns a failure <see cref="OperationResult"/> if
    /// unavailable, or <c>null</c> if it is fine to proceed.
    /// </summary>
    private OperationResult? CheckPortAvailable(string functionName)
    {
        if (_wasOpenBeforeDisconnect.ContainsKey(functionName))
            return OperationResult.Failure(
                $"Port for function '{functionName}' is disconnected — " +
                $"waiting for device to reconnect");

        if (_registry.ResolvePortPath(functionName) is null)
            return OperationResult.Failure(
                $"Port for function '{functionName}' is not available — " +
                $"device may be disconnected");

        return null;
    }

    /// <summary>
    /// Returns a <see cref="ChannelReader{T}"/> that yields only messages
    /// whose <see cref="SerialMessage.FunctionName"/> is in
    /// <paramref name="functionNames"/>.  Pass an empty enumerable to receive
    /// all messages including SYSTEM status messages.
    /// Each call creates a dedicated filtered channel — the caller owns it.
    /// </summary>
    public ChannelReader<SerialMessage> GetMessageReader(IEnumerable<string> functionNames)
    {
        var filter = functionNames.ToHashSet(StringComparer.OrdinalIgnoreCase);

        var filtered = Channel.CreateUnbounded<SerialMessage>(new UnboundedChannelOptions
        { SingleWriter = true, SingleReader = false });

        _ = Task.Run(async () =>
        {
            await foreach (var msg in _broadcastChannel.Reader.ReadAllAsync())
            {
                _logger.LogDebug(
                    "GetMessageReader received: {Function} {Text}",
                    msg.FunctionName, msg.Text);

                // Empty set = subscribe to ALL (wildcard), non-empty = filter.
                // SYSTEM messages always pass through to every subscriber.
                if (filter.Count == 0
                    || filter.Contains(msg.FunctionName)
                    || msg.FunctionName.Equals(SystemFunction, StringComparison.OrdinalIgnoreCase))
                {
                    await filtered.Writer.WriteAsync(msg);
                }
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
            "Starting auto-discovery (AutoOpen={AutoOpen})", autoOpen);

        var devices = await UsbSerialPortMapper.GetUsbSerialPortsAsync(token);

        int opened = 0;

        foreach (UsbSerialPortInfo identifier in devices)
        {
            var existing = _registry.AllRegistrations
                .FirstOrDefault(r => r.Identifier.Key == identifier.Key);

            if (existing is null)
            {
                _logger.LogDebug(
                    "Auto-discovery found unregistered device {Key} on {Port}",
                    identifier.Key, identifier.PortName);
                continue;
            }

            _registry.SetPortPath(identifier.Key, identifier.PortName);

            if (!autoOpen || _workers.ContainsKey(existing.FunctionName)) continue;
            var result = await OpenPortAsync(existing.FunctionName, token);

            if (result.IsSuccess) opened++;
        }

        _logger.LogInformation(
            "Completed auto-discovery: {OpenedCount} ports opened", opened);

        return OperationResultWithValue<int>.Success(opened);
    }

    // ── Command dispatcher ────────────────────────────────────────────────────

    public Task<OperationResult> ExecuteAsync(
        ISerialCommand command, CancellationToken token = default)
        => command.ExecuteAsync(this, token);

    // ── Shutdown ──────────────────────────────────────────────────────────────

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("Stopping serial port manager service");

        var tasks = _workers.Values.Select(async w =>
        {
            await w.StopAsync();
            await w.DisposeAsync();
        });

        await Task.WhenAll(tasks);
        _workers.Clear();
        _wasOpenBeforeDisconnect.Clear();
        _broadcastChannel.Writer.TryComplete();

        await base.StopAsync(cancellationToken);

        _logger.LogInformation(
            "Completed stopping serial port manager service: {Success}", true);
    }

    // ── Internal helpers ──────────────────────────────────────────────────────

    private void SubscribeWorkerToBroadcast(
        string function, ISerialWorker worker, CancellationToken token)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                await worker.Started.WaitAsync(token);
            }
            catch (OperationCanceledException) { return; }
            catch (Exception ex)
            {
                _logger.LogError(
                    ex, "Worker {Function} failed before fan-in could subscribe", function);
                return;
            }

            _logger.LogDebug("Subscribed worker {Function} to broadcast", function);

            try
            {
                await foreach (var msg in worker.ReceivedMessages.ReadAllAsync(token))
                {
                    _logger.LogDebug(
                        "Fan-in writing to broadcast: {Function} {Text}",
                        msg.FunctionName, msg.Text);

                    await _broadcastChannel.Writer.WriteAsync(msg, token);
                }
            }
            catch (OperationCanceledException) { return; }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Fan-in failure for {Function}", function);
            }

            _logger.LogDebug("Unsubscribed worker {Function} from broadcast", function);

            // If the app is shutting down, the channel completing is expected — ignore.
            if (token.IsCancellationRequested) return;

            // Worker channel completed while the app is still running — the physical
            // device was unplugged.  Use TryAdd so only one poll loop starts per
            // function even if the fan-in task and HandlePortRemovedAsync both fire.
            if (!_wasOpenBeforeDisconnect.TryAdd(function, true))
            {
                _logger.LogDebug(
                    "Worker {Function} channel completed but disconnect already recorded — " +
                    "poll loop already running",
                    function);
                return;
            }

            _logger.LogWarning(
                "Worker channel completed unexpectedly for {Function} — " +
                "device disconnected, starting reconnect poll",
                function);

            // Clean up the dead worker so OpenPortAsync can create a fresh one.
            _workers.TryRemove(function, out var deadWorker);
            if (deadWorker is not null)
            {
                try { await deadWorker.StopAsync(); } catch { }
                try { await deadWorker.DisposeAsync(); } catch { }
            }

            var regKey = _registry.ResolveKeyForFunction(function);
            if (regKey is not null)
                _registry.ClearPortPath(regKey);

            await PublishStatusMessageAsync(
                function,
                SerialMessageKind.Disconnected,
                "Device disconnected unexpectedly",
                CancellationToken.None);

            _ = Task.Run(
                () => PollForReconnectAsync(function, _appToken),
                _appToken);

        }, token);
    }

    /// <summary>
    /// Polls the OS every <see cref="ReconnectPollInterval"/> until the device
    /// reappears by hardware <c>Key</c>, then reopens the port.
    /// This is the primary reconnect path on Windows where the WMI registry
    /// diff cannot reliably detect removal.
    /// </summary>
    private async Task PollForReconnectAsync(string function, CancellationToken token)
    {
        _logger.LogInformation(
            "Starting reconnect poll for {Function}", function);

        var reg = _registry.AllRegistrations.FirstOrDefault(r => r.FunctionName == function);
        if (reg is null)
        {
            _logger.LogWarning(
                "Reconnect poll {Function}: {Success}. Reason: Not registered",
                function, false);
            _wasOpenBeforeDisconnect.TryRemove(function, out _);
            return;
        }

        while (!token.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(ReconnectPollInterval, token);
            }
            catch (OperationCanceledException) { break; }

            // Stop polling if something else (e.g. HandlePortAddedAsync) already
            // reopened the port.
            if (_workers.ContainsKey(function))
            {
                _logger.LogInformation(
                    "Reconnect poll {Function}: port already open — stopping poll",
                    function);
                _wasOpenBeforeDisconnect.TryRemove(function, out _);
                return;
            }

            _logger.LogDebug(
                "Reconnect poll {Function}: scanning for device key {Key}",
                function, reg.Key);

            try
            {
                var devices = await UsbSerialPortMapper.GetUsbSerialPortsAsync(token);
                var match = devices.FirstOrDefault(d => d.Key == reg.Key);

                if (match is null)
                {
                    _logger.LogDebug(
                        "Reconnect poll {Function}: device not yet visible on OS",
                        function);
                    continue;
                }

                _logger.LogInformation(
                    "Reconnect poll {Function}: device found on {Port} — reopening",
                    function, match.PortName);

                // Update port path in case Windows assigned a different COM number.
                _registry.SetPortPath(match.Key, match.PortName);

                // Clear the flag before opening so CheckPortAvailable passes.
                _wasOpenBeforeDisconnect.TryRemove(function, out _);

                var result = await OpenPortAsync(function, token);

                if (result.IsSuccess)
                {
                    await PublishStatusMessageAsync(
                        function,
                        SerialMessageKind.Reconnected,
                        $"Device reconnected and port reopened on {match.PortName}",
                        token);

                    _logger.LogInformation(
                        "Completed reconnect poll {Function} on {Port}: {Success}",
                        function, match.PortName, true);
                    return;
                }

                // Reopen failed — put the flag back and keep polling.
                _wasOpenBeforeDisconnect.TryAdd(function, true);

                _logger.LogWarning(
                    "Reconnect poll {Function}: reopen on {Port} failed ({Message}) — " +
                    "continuing poll",
                    function, match.PortName, result.Message);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                _logger.LogError(
                    ex,
                    "Reconnect poll {Function}: error during device scan",
                    function);
            }
        }

        _logger.LogInformation(
            "Reconnect poll {Function}: stopped (cancellation requested)",
            function);
    }

    /// <summary>
    /// Writes a structured status message to the broadcast channel so all
    /// <see cref="GetMessageReader"/> subscribers receive disconnect / reconnect
    /// notifications in-band alongside normal serial data.
    /// </summary>
    private async Task PublishStatusMessageAsync(
        string functionName,
        SerialMessageKind kind,
        string description,
        CancellationToken token)
    {
        var payload = JsonSerializer.Serialize(new
        {
            type = kind.ToString().ToUpperInvariant(),
            function = functionName,
            message = description,
            timestamp = DateTimeOffset.UtcNow
        });

        var payloadBytes = System.Text.Encoding.UTF8.GetBytes(payload);
        var portPath = _registry.ResolvePortPath(functionName) ?? "(disconnected)";

        var msg = new SerialMessage
        {
            FunctionName = SystemFunction,
            PortPath = portPath,
            Payload = payloadBytes,
            Text = payload
        };

        _logger.LogDebug(
            "Publishing status message {Kind} for {Function}: {Description}",
            kind, functionName, description);

        try
        {
            await _broadcastChannel.Writer.WriteAsync(msg, token);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "Failed to publish status message {Kind} for {Function}",
                kind, functionName);
        }
    }

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

// ═════════════════════════════════════════════════════════════════════════════
// Supporting types
// ═════════════════════════════════════════════════════════════════════════════

/// <summary>
/// Discriminates in-band status messages from normal serial data on the
/// broadcast channel.  Normal data uses <see cref="Data"/>.
/// </summary>
public enum SerialMessageKind
{
    Data,
    Disconnected,
    Reconnected,
    Error
}




