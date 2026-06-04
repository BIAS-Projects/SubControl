using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using Microsoft.Extensions.Logging;

using SubConsole.Models;
using SubControlMAUI.Messages;
using SubControlMAUI.Models;
using SubControlMAUI.Services;
using System.Diagnostics;
using System.Text.Json;

namespace SubControlMAUI.ViewModels;

public partial class RotatorViewModel : BaseViewModel
{
    // ─────────────────────────────────────────────────────────────────────────
    // Immutable holder for a single pending push-confirm operation.
    // Swapping the whole object atomically via Interlocked.CompareExchange means
    // there is no torn-state window where a new TCS is paired with an old predicate.
    // ─────────────────────────────────────────────────────────────────────────
    private sealed class PendingConfirm
    {
        public string Function { get; }
        public Func<string, bool> Predicate { get; }
        public TaskCompletionSource<bool> Tcs { get; }

        public PendingConfirm(string function, Func<string, bool> predicate)
        {
            Function = function;
            Predicate = predicate;
            Tcs = new TaskCompletionSource<bool>(
                            TaskCreationOptions.RunContinuationsAsynchronously);
        }
    }
    // ── services ──────────────────────────────────────────────────────────────
    private readonly ILogger<RotatorViewModel> _logger;
    private readonly IMessenger _messenger;
    private readonly TcpSocketService _tcp;
    private readonly IAlertService _alertService;

    public ApplicationStateService AppState { get; }

    // ── command serialisation ─────────────────────────────────────────────────
    // Only one command (or poll) may be in-flight at a time.
    private readonly SemaphoreSlim _commandLock = new(1, 1);

    // ── pending operation (atomic swap, no torn state) ────────────────────────
    // Written only inside _commandLock; read from any thread via Interlocked.
    private PendingConfirm? _pendingConfirm;

    // ── polling ───────────────────────────────────────────────────────────────
    private CancellationTokenSource? _pollingCts;
    private readonly TimeSpan _pollingInterval = TimeSpan.FromMilliseconds(500);
    private bool _isPageVisible;

    // ── UI-update throttle ────────────────────────────────────────────────────
    // Prevents the dispatcher queue from being flooded by high-frequency packets.
    private long _lastStatusTick;
    private const long StatusThrottleMs = 100;

    // ── protocol timeouts ─────────────────────────────────────────────────────
    // Poll timeout is short so the lock is released quickly between polls,
    // keeping the command path responsive.
    private readonly TimeSpan _commandTimeout = TimeSpan.FromSeconds(10);
    private readonly TimeSpan _pollTimeout = TimeSpan.FromSeconds(1.5);
    // How long a user command will wait for a poll to finish before giving up.
    // Needs to be >= _pollingInterval + _pollTimeout to let one full poll cycle
    // complete before a command is rejected.
    private readonly TimeSpan _lockWaitTimeout = TimeSpan.FromSeconds(2);

    // ─────────────────────────────────────────────────────────────────────────
    public RotatorViewModel(
        IMessenger messenger,
        ILogger<RotatorViewModel> logger,
        TcpSocketService tcp,
        ApplicationStateService appState,
        IAlertService alertService)
    {
        Title = "Rotator Control";
        _logger = logger;
        _messenger = messenger;
        AppState = appState;
        _tcp = tcp;
        _alertService = alertService;
        ArmAngle = 73;
        StatusText = "";

        MinRotatorValue = Rotator.MinRotatorValue;
        MaxRotatorValue = Rotator.MaxRotatorValue;
        AdjustValue = Rotator.AdjustValue;

        RegisterMessages();
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Message registration
    // ─────────────────────────────────────────────────────────────────────────
    private void RegisterMessages()
    {
        // IMPORTANT: the handler is NOT async – it fires a background Task so
        // exceptions are observable and handlers never overlap concurrently.
        _messenger.Register<TcpDataReceivedMessage>(this, (_, msg) =>
            _ = HandleTcpDataAsync(msg));

        _messenger.Register<TcpSendRequestMessage>(this, (_, _) => { /* intentionally empty */ });

        _messenger.Register<TcpStatusMessage>(this, (_, msg) =>
            SetStatus(msg.Value));

        _messenger.Register<TcpErrorMessage>(this, (_, msg) =>
        {
            _logger.LogError("TcpErrorMessage: {Message}", msg.Value.Message);
            SetStatus(msg.Value.Message);
        });

        _messenger.Register<TcpAckTimeoutMessage>(this, (_, msg) =>
            SetStatus($"No response to: {msg.Command}"));

        _messenger.Register<TcpNackMessage>(this, (_, msg) =>
            SetStatus($"Server rejected '{msg.Command}': {msg.Reason}"));

        _messenger.Register<TcpIsConnected>(this, (_, msg) =>
        {
            if (!msg.Value)
                MainThread.BeginInvokeOnMainThread(async () =>
                    await Shell.Current.GoToAsync("//MainPage"));
        });
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Core TCP data handler – runs on a ThreadPool thread, not the UI thread.
    // All shared state mutations go through _pendingConfirm (atomic swap).
    // ─────────────────────────────────────────────────────────────────────────
    private Task HandleTcpDataAsync(TcpDataReceivedMessage msg)
    {
        try
        {
            var function = msg.Value.Function;
            var raw = msg.Value.Data;

            if (!function.Equals(Feature.RotatorName, StringComparison.OrdinalIgnoreCase))
            {
                _logger.LogTrace("ROTATOR handler: ignored function='{Function}'", function);
                return Task.CompletedTask;
            }

            var data = raw?.Trim();
            if (string.IsNullOrWhiteSpace(data))
            {
                _logger.LogTrace("ROTATOR handler: empty data, ignored");
                return Task.CompletedTask;
            }

            // ── protocol framing checks ───────────────────────────────────────
            bool hasValidHeader = Rotator.Headers.Any(h =>
                data.StartsWith(h, StringComparison.OrdinalIgnoreCase));

            bool hasValidTerminator = Rotator.Terminators.Any(t =>
                data.EndsWith(t, StringComparison.OrdinalIgnoreCase));

            if (!hasValidHeader || !hasValidTerminator)
            {
                _logger.LogWarning(
                    "ROTATOR handler: frame rejected — data='{Data}' header={H} terminator={T}",
                    data, hasValidHeader, hasValidTerminator);
                return Task.CompletedTask;
            }

            // ── throttled UI status update ────────────────────────────────────
            long now = Environment.TickCount64;
            if (Interlocked.Read(ref _lastStatusTick) is var last
                && (now - last) >= StatusThrottleMs)
            {
                Interlocked.Exchange(ref _lastStatusTick, now);
                SetStatus(data);
            }

            // ── encoder poll response ─────────────────────────────────────────
            if (MatchesCommandCode(data, "MRL"))
                UpdateEncoderAngle(data);

            // ── push-confirm match ────────────────────────────────────────────
            var pending = Interlocked.CompareExchange(ref _pendingConfirm, null, null);

            _logger.LogDebug(
                "ROTATOR handler: data='{Data}' len={Len} code='{Code}' pending={Pending}",
                data,
                data.Length,
                data.Length >= 5 ? data.Substring(2, Math.Min(3, data.Length - 2)) : "??",
                pending is null ? "none" : $"waiting for function='{pending.Function}'");

            if (pending is not null)
            {
                bool functionMatch = function.Equals(pending.Function, StringComparison.OrdinalIgnoreCase);
                bool predicateMatch = pending.Predicate(data);

                _logger.LogDebug(
                    "ROTATOR handler: confirm check — functionMatch={FM} predicateMatch={PM}",
                    functionMatch, predicateMatch);

                if (functionMatch && predicateMatch)
                {
                    _logger.LogInformation(
                        "ROTATOR handler: confirm signalled for data='{Data}'", data);
                    pending.Tcs.TrySetResult(true);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "HandleTcpDataAsync threw unexpectedly");
        }

        return Task.CompletedTask;
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Observable properties
    // ─────────────────────────────────────────────────────────────────────────
    [ObservableProperty] private double buttonSize;
    [ObservableProperty] private double layoutSpacing;
    [ObservableProperty] private string statusText = "Stopped";
    [ObservableProperty] private double armAngle;
    [ObservableProperty] 
    public int minRotatorValue = 0;

    [ObservableProperty]
    public int maxRotatorValue = 90;

    [ObservableProperty]
    public int adjustValue  = 1;


    partial void OnArmAngleChanged(double value)
    {
        value = Math.Clamp(value, 0, 180);
        // TODO: send angle to hardware if required
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Commands – all follow the same pattern:
    //   1. Wait briefly for the lock (queues behind an in-progress poll)
    //   2. Set IsBusy
    //   3. Send + wait for push-confirm
    //   4. Release lock in finally
    //
    // Protocol (Sidus Solutions): the device echoes the command mnemonic back
    // with the current encoder position in the data field, e.g.
    //   Sent:     #AMST0000W   (stop)
    //   Response: #AMST5186W   (stopped at encoder count 5186)
    // MML (move-to-location) responses echo MML with the target position.
    // MMF/MMB responses echo MMF/MMB.
    // ─────────────────────────────────────────────────────────────────────────
    [RelayCommand]
    private async Task Park()
        => await RunCommandAsync(
               "Parking rotator...",
               "Rotator parking...",
               "Park rotator command failed",
               Rotator.ParkMotorA,
               // Protocol sends MML twice: first is ACK, second is arrival.
               // We confirm on the first (ACK) and release the lock immediately
               // so the user can reposition or stop during the move.
               data => MatchesCommandCode(data, "MML"));

    [RelayCommand]
    private async Task Deploy()
        => await RunCommandAsync(
               "Deploying rotator...",
               "Rotator deploying...",
               "Deploy rotator command failed",
               Rotator.DeployMotorA,
               data => MatchesCommandCode(data, "MML"));

    [RelayCommand]
    private async Task Forward()
        => await RunCommandAsync(
               "Driving rotator forward...",
               "Rotator forward command confirmed",
               "Forward rotator command failed",
               Rotator.PanMotorAForward,
               data => MatchesCommandCode(data, "MMF"));

    [RelayCommand]
    private async Task Backward()
        => await RunCommandAsync(
               "Driving rotator backward...",
               "Rotator backward command confirmed",
               "Backward rotator command failed",
               Rotator.PanMotorABackward,
               data => MatchesCommandCode(data, "MMB"));

    [RelayCommand]
    private async Task Stop()
        => await RunCommandAsync(
               "Stopping rotator...",
               "Rotator stop command confirmed",
               "Stop rotator command failed",
               Rotator.StopPanMotorA,
               data => MatchesCommandCode(data, "MST"));

    // ─────────────────────────────────────────────────────────────────────────
    // Shared command runner
    // ─────────────────────────────────────────────────────────────────────────
    private async Task RunCommandAsync(
        string pendingMessage,
        string successMessage,
        string failureMessage,
        string payload,
        Func<string, bool> confirmPredicate)
    {
        // Wait briefly so commands can queue behind a poll rather than
        // immediately failing when the user taps while a poll is in-flight.
        if (!await _commandLock.WaitAsync(_lockWaitTimeout))
        {
            SetStatus("Command already in progress");
            return;
        }

        IsBusy = true;
        try
        {
            SetStatus(pendingMessage);

            bool ok = await SendAndWaitForConfirmAsync(
                Feature.RotatorName, "WRITE TEXT", payload,
                Feature.RotatorName, confirmPredicate,
                _commandTimeout);

            SetStatus(ok ? successMessage : failureMessage);
        }
        finally
        {
            IsBusy = false;
            _commandLock.Release();
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Send a command and wait for a matching push-confirm.
    // Must be called while holding _commandLock.
    // Uses a single atomic swap for the PendingConfirm object, so there is no
    // window where TCS, function, predicate, and id are mismatched.
    // ─────────────────────────────────────────────────────────────────────────
    private async Task<bool> SendAndWaitForConfirmAsync(
        string sendFeature,
        string sendCommand,
        string sendData,
        string confirmFunction,
        Func<string, bool> confirmPredicate,
        TimeSpan timeout)
    {
        // Install the pending operation atomically BEFORE sending, so we cannot
        // miss a fast response that arrives before WhenAny is evaluated.
        var op = new PendingConfirm(confirmFunction, confirmPredicate);
        Interlocked.Exchange(ref _pendingConfirm, op);

        try
        {
            bool sent = await _tcp.SendCommandAsync(
                new TCPMessageBody<string>(sendFeature, sendCommand, sendData),
                CancellationToken.None);

            if (!sent)
                return false;

            var completed = await Task.WhenAny(op.Tcs.Task, Task.Delay(timeout));

            return completed == op.Tcs.Task && op.Tcs.Task.Result;
        }
        finally
        {
            // Clear only if we are still the active operation (another command
            // could theoretically have replaced us, though the lock prevents it).
            Interlocked.CompareExchange(ref _pendingConfirm, null, op);
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Polling
    // ─────────────────────────────────────────────────────────────────────────
    public void OnAppearing()
    {
        _isPageVisible = true;

        _pollingCts?.Cancel();
        _pollingCts = new CancellationTokenSource();
        _ = StartPollingAsync(_pollingCts.Token);
    }

    public void OnDisappearing()
    {
        _isPageVisible = false;
        _pollingCts?.Cancel();
        _pollingCts?.Dispose();
        _pollingCts = null;
    }

    private async Task StartPollingAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            try
            {
                if (_isPageVisible && !IsBusy)
                    await PollEncoderAsync(token);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Rotator polling failed");
                SetStatus(ex.Message);
            }

            try
            {
                await Task.Delay(_pollingInterval, token);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    // Polls the encoder by acquiring the lock, sending, and waiting for the MRL
    // response before releasing.  This prevents poll responses from interleaving
    // with command responses and stops the "firehose" of unacknowledged requests.
    private async Task PollEncoderAsync(CancellationToken token)
    {
        // Skip this poll cycle if a command is in progress.
        if (!await _commandLock.WaitAsync(TimeSpan.Zero, token))
            return;

        try
        {
            var op = new PendingConfirm(Feature.RotatorName, data => MatchesCommandCode(data, "MRL"));
            Interlocked.Exchange(ref _pendingConfirm, op);

            try
            {
                bool sent = await _tcp.SendCommandAsync(
                    new TCPMessageBody<string>(Feature.RotatorName, "WRITE TEXT", Rotator.EncoderLocationA),
                    token);

                if (!sent)
                    return;

                // Short timeout so the lock is released quickly between poll cycles,
                // keeping the command path responsive. UpdateEncoderAngle signals
                // the TCS as soon as the MRL packet arrives.
                var completed = await Task.WhenAny(op.Tcs.Task, Task.Delay(_pollTimeout, token));

                if (completed != op.Tcs.Task)
                    _logger.LogWarning("Encoder poll timed out");
            }
            finally
            {
                Interlocked.CompareExchange(ref _pendingConfirm, null, op);
            }
        }
        catch (OperationCanceledException)
        {
            // Page is going away – normal, not an error.
        }
        finally
        {
            _commandLock.Release();
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Encoder angle parsing
    // ─────────────────────────────────────────────────────────────────────────
    private void UpdateEncoderAngle(string response)
    {
        try
        {
            // Expected format: #AMRL6274R  (fixed-width: 2 prefix + 3 cmd + 4 digits + 1 terminator)
            response = response.Trim();
            if (response.Length < 10) return;

            string command = response.Substring(2, 3);
            if (!command.Equals("MRL", StringComparison.OrdinalIgnoreCase)) return;

            string digits = response.Substring(5, 4);
            if (!int.TryParse(digits, out int encoder)) return;

            double degrees = Math.Clamp((encoder - 5000) * 0.0879, 0, 180);

            MainThread.BeginInvokeOnMainThread(() => ArmAngle = degrees);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed parsing encoder response '{Response}'", response);
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Helpers
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>Exact match (ignoring trailing CR/LF) for command responses.</summary>
    private static bool MatchesResponse(string received, string expected)
        => received.Trim().Equals(expected.TrimEnd('\r', '\n'), StringComparison.Ordinal);

    /// <summary>Substring match for 3-character command codes embedded in a packet.</summary>
    private static bool MatchesCommand(string received, string command)
        => received.Contains(command, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Structural match for the 3-character command code at bytes [2..4] of the
    /// fixed-width protocol frame (#AMRL6274R / #AMST5186W etc.).
    /// Safer than Contains() — an MRL packet can never match "MST" and vice versa.
    /// </summary>
    private static bool MatchesCommandCode(string received, string code)
    {
        received = received.Trim();
        // Minimum frame: 2 prefix + 3 code + 4 digits + 1 terminator = 10 chars
        return received.Length >= 10
            && received.Substring(2, 3).Equals(code, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Thread-safe status update, marshalled to the UI thread.
    /// Callers do NOT need to wrap in BeginInvokeOnMainThread themselves.
    /// </summary>
    private void SetStatus(string text)
    {
        if (MainThread.IsMainThread)
            StatusText = text;
        else
            MainThread.BeginInvokeOnMainThread(() => StatusText = text);
    }
}