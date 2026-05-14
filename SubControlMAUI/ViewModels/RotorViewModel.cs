using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using Microsoft.Extensions.Logging;

using SubConsole.Models;
using SubControlMAUI.Messages;
using SubControlMAUI.Models;
using SubControlMAUI.Services;

namespace SubControlMAUI.ViewModels;

// ─────────────────────────────────────────────────────────────────────────────
// DEMO MODE – fire-and-forget command dispatch.
//
// Commands are sent and immediately return; no lock, no TCS, no timeout wait.
// Position feedback comes entirely from the encoder poll (MRL packets).
//
// Logging strategy:
//   [SEND #NNNNN hh:mm:ss.fff]  every outgoing command, including polls
//   [RECV #NNNNN hh:mm:ss.fff]  every incoming packet, parsed into fields
//
// Sequence numbers are independent for send and receive so you can spot:
//   - Lost packets   : gap in RECV sequence
//   - Corrupt frames : "Frame rejected" warning
//   - Mis-ordering   : RECV timestamps out of order relative to SEND timestamps
// ─────────────────────────────────────────────────────────────────────────────
public partial class RotorViewModel : BaseViewModel
{
    // ── services ──────────────────────────────────────────────────────────────
    private readonly ILogger<RotorViewModel> _logger;
    private readonly IMessenger _messenger;
    private readonly TcpSocketService _tcp;
    private readonly IAlertService _alertService;

    public ApplicationStateService AppState { get; }

    // ── polling ───────────────────────────────────────────────────────────────
    private CancellationTokenSource? _pollingCts;
    private readonly TimeSpan _pollingInterval = TimeSpan.FromMilliseconds(500);
    private bool _isPageVisible;

    // ── UI-update throttle ────────────────────────────────────────────────────
    // Prevents the dispatcher queue from being flooded by high-frequency packets.
    private long _lastStatusTick;
    private const long StatusThrottleMs = 100;

    // ── sequence counters (for log correlation) ───────────────────────────────
    // SEPARATE counters — send and receive are independent series.
    private long _sendSeq;
    private long _recvSeq;

    // ── command debounce ──────────────────────────────────────────────────────
    // Prevents rapid button presses flooding the device input buffer, which
    // causes it to drop encoder poll responses entirely.
    private long _lastCommandTick;
    private const long CommandDebounceMs = 400;

    // ─────────────────────────────────────────────────────────────────────────
    public RotorViewModel(
        IMessenger messenger,
        ILogger<RotorViewModel> logger,
        TcpSocketService tcp,
        ApplicationStateService appState,
        IAlertService alertService)
    {
        Title = "Rotor Control";
        _logger = logger;
        _messenger = messenger;
        AppState = appState;
        _tcp = tcp;
        _alertService = alertService;
        ArmAngle = 73;
        StatusText = "";

        RegisterMessages();
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Message registration
    // ─────────────────────────────────────────────────────────────────────────
    private void RegisterMessages()
    {
        // NOT async — fires a background Task so exceptions are catchable and
        // handlers never overlap for the same message type.
        _messenger.Register<TcpDataReceivedMessage>(this, (_, msg) =>
            _ = HandleTcpDataAsync(msg));

        _messenger.Register<TcpSendRequestMessage>(this, (_, _) => { });

        _messenger.Register<TcpStatusMessage>(this, (_, msg) =>
        {
            _logger.LogInformation("[TCP STATUS] {Message}", msg.Value);
            SetStatus(msg.Value);
        });

        _messenger.Register<TcpErrorMessage>(this, (_, msg) =>
        {
            _logger.LogError("[TCP ERROR] {Message}", msg.Value.Message);
            SetStatus(msg.Value.Message);
        });

        _messenger.Register<TcpAckTimeoutMessage>(this, (_, msg) =>
        {
            _logger.LogWarning("[TCP TIMEOUT] No response to: '{Command}'", msg.Command);
            SetStatus($"No response to: {msg.Command}");
        });

        _messenger.Register<TcpNackMessage>(this, (_, msg) =>
        {
            _logger.LogWarning("[TCP NACK] Rejected '{Command}': {Reason}", msg.Command, msg.Reason);
            SetStatus($"Server rejected '{msg.Command}': {msg.Reason}");
        });

        _messenger.Register<TcpIsConnected>(this, (_, msg) =>
        {
            _logger.LogInformation("[TCP CONNECTED] Value={Value}", msg.Value);
            if (!msg.Value)
                MainThread.BeginInvokeOnMainThread(async () =>
                    await Shell.Current.GoToAsync("//MainPage"));
        });
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Incoming packet handler
    //
    // Every packet is logged with a receive-sequence number and wall-clock
    // timestamp. The Sidus fixed-width frame is parsed into named fields so
    // corrupt or truncated packets are immediately obvious.
    //
    // Sidus frame layout (10 chars + CRLF):
    //   [0]     HDR  : '#' pan, '$' tilt, etc.
    //   [1]     ADDR : node address A-Z
    //   [2..4]  CMD  : 3-char mnemonic (MRL, MST, MMF, …)
    //   [5..8]  DATA : 4 decimal digits
    //   [9]     TERM : 'R' read / 'W' write  (lowercase pre-index on Rev G+)
    // ─────────────────────────────────────────────────────────────────────────
    private Task HandleTcpDataAsync(TcpDataReceivedMessage msg)
    {
        try
        {
            var function = msg.Value.Function;
            var raw = msg.Value.Data;
            var recvSeq = Interlocked.Increment(ref _recvSeq);
            var ts = DateTimeOffset.Now;

            // Log every packet so non-ROTOR traffic is visible too.
            _logger.LogDebug(
                "[RECV R#{Seq:D5} {Ts:HH:mm:ss.fff}] function='{Function}' raw='{Raw}'",
                recvSeq, ts, function, raw?.Trim());

            if (!function.Equals("ROTOR", StringComparison.OrdinalIgnoreCase))
                return Task.CompletedTask;

            var data = raw?.Trim();
            if (string.IsNullOrWhiteSpace(data))
            {
                _logger.LogWarning(
                    "[RECV R#{Seq:D5}] ROTOR packet has empty data — possible framing error",
                    recvSeq);
                return Task.CompletedTask;
            }

            // ── framing validation ────────────────────────────────────────────
            bool hasValidHeader = Rotor.Headers.Any(h =>
                data.StartsWith(h, StringComparison.OrdinalIgnoreCase));

            bool hasValidTerminator = Rotor.Terminators.Any(t =>
                data.EndsWith(t, StringComparison.OrdinalIgnoreCase));

            if (!hasValidHeader || !hasValidTerminator)
            {
                _logger.LogWarning(
                    "[RECV R#{Seq:D5}] Frame rejected — data='{Data}' len={Len} " +
                    "validHeader={H} validTerminator={T}",
                    recvSeq, data, data.Length, hasValidHeader, hasValidTerminator);
                return Task.CompletedTask;
            }

            // ── parse fixed-width Sidus frame ─────────────────────────────────
            string hdr = data.Substring(0, 1);
            string addr = data.Length >= 2 ? data.Substring(1, 1) : "?";
            string cmdCode = data.Length >= 5 ? data.Substring(2, 3) : "???";
            string dataField = data.Length >= 9 ? data.Substring(5, 4) : "????";
            char term = data.Length >= 10 ? data[9] : '?';

            // Warn on lowercase terminator — Rev G+ firmware signals pre-index state.
            if (term is 'w' or 'r')
            {
                _logger.LogWarning(
                    "[RECV R#{Seq:D5}] Lowercase terminator '{Term}' — " +
                    "unit has NOT yet passed index (position unreliable until homed)",
                    recvSeq, term);
            }

            _logger.LogInformation(
                "[RECV R#{Seq:D5} {Ts:HH:mm:ss.fff}] hdr='{Hdr}' addr='{Addr}' " +
                "cmd='{Cmd}' data='{DataField}' term='{Term}'",
                recvSeq, ts, hdr, addr, cmdCode, dataField, term);

            // ── throttled UI status update ────────────────────────────────────
            long now = Environment.TickCount64;
            if (Interlocked.Read(ref _lastStatusTick) is var last
                && (now - last) >= StatusThrottleMs)
            {
                Interlocked.Exchange(ref _lastStatusTick, now);
                SetStatus(data);
            }

            // ── encoder position update ───────────────────────────────────────
            if (cmdCode.Equals("MRL", StringComparison.OrdinalIgnoreCase))
                UpdateEncoderAngle(dataField, recvSeq);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[RECV] HandleTcpDataAsync threw unexpectedly");
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

    partial void OnArmAngleChanged(double value)
    {
        value = Math.Clamp(value, 0, 180);
        // TODO: send angle to hardware if required
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Commands – fire and forget
    // Send and return immediately. No lock, no confirmation wait.
    // The UI stays fully interactive throughout.
    // ─────────────────────────────────────────────────────────────────────────
    [RelayCommand]
    private async Task Park()
        => await FireCommandAsync("Park", Rotor.ParkMotorA);

    [RelayCommand]
    private async Task Deploy()
        => await FireCommandAsync("Deploy", Rotor.DeployMotorA);

    [RelayCommand]
    private async Task Forward()
        => await FireCommandAsync("Forward", Rotor.PanMotorAForward);

    [RelayCommand]
    private async Task Backward()
        => await FireCommandAsync("Backward", Rotor.PanMotorABackward);

    [RelayCommand]
    private async Task Stop()
        => await FireCommandAsync("Stop", Rotor.StopPanMotorA);

    // ─────────────────────────────────────────────────────────────────────────
    // Core send helper – debounced to prevent device input buffer flooding.
    // Logs every outgoing command with a sequence number and timestamp.
    // ─────────────────────────────────────────────────────────────────────────
    private async Task FireCommandAsync(string commandName, string payload)
    {
        // Debounce — ignore presses that arrive within CommandDebounceMs of the
        // last accepted command. Uses a lock-free CAS swap on the tick counter.
        long now = Environment.TickCount64;
        long last = Interlocked.Read(ref _lastCommandTick);
        if (now - last < CommandDebounceMs)
        {
            _logger.LogDebug(
                "[SEND DEBOUNCED] '{Name}' ignored — {Elapsed}ms since last command (min {Min}ms)",
                commandName, now - last, CommandDebounceMs);
            return;
        }
        Interlocked.Exchange(ref _lastCommandTick, now);

        var seq = Interlocked.Increment(ref _sendSeq);
        var ts = DateTimeOffset.Now;

        _logger.LogInformation(
            "[SEND S#{Seq:D5} {Ts:HH:mm:ss.fff}] command='{Name}' payload='{Payload}'",
            seq, ts, commandName, payload.TrimEnd('\r', '\n'));

        SetStatus($"{commandName}...");
        IsBusy = true;

        bool sent = await _tcp.SendCommandAsync(
            new TCPMessageBody<string>("ROTOR", "WRITE TEXT", payload),
            CancellationToken.None);

        if (sent)
        {
            _logger.LogInformation(
                "[SEND S#{Seq:D5}] '{Name}' handed to TCP layer successfully", seq, commandName);

            // Hold IsBusy for the remainder of the debounce window so the UI
            // reflects that input is locked out. Any time already spent in
            // SendCommandAsync counts toward the window.
            long remaining = CommandDebounceMs - (Environment.TickCount64 - now);
            if (remaining > 0)
                await Task.Delay((int)remaining);
        }
        else
        {
            _logger.LogWarning(
                "[SEND S#{Seq:D5}] '{Name}' REJECTED by TCP layer — command not sent",
                seq, commandName);
            SetStatus($"{commandName} failed — TCP rejected");
        }

        IsBusy = false;
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Polling – fires encoder read every 500 ms.
    // Poll sends appear in the log at Debug level so they don't drown out
    // command sends (which are at Information level) but are still visible
    // when you need to check poll timing.
    // ─────────────────────────────────────────────────────────────────────────
    public void OnAppearing()
    {
        _isPageVisible = true;
        _pollingCts?.Cancel();
        _pollingCts = new CancellationTokenSource();
        _ = StartPollingAsync(_pollingCts.Token);
        _logger.LogInformation("[POLL] Polling started");
    }

    public void OnDisappearing()
    {
        _isPageVisible = false;
        _pollingCts?.Cancel();
        _pollingCts?.Dispose();
        _pollingCts = null;
        _logger.LogInformation("[POLL] Polling stopped");
    }

    private async Task StartPollingAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            try
            {
                if (_isPageVisible)
                    await PollEncoderAsync(token);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[POLL] Polling loop threw unexpectedly");
                SetStatus(ex.Message);
            }

            try { await Task.Delay(_pollingInterval, token); }
            catch (OperationCanceledException) { break; }
        }

        _logger.LogInformation("[POLL] Polling loop exited");
    }

    private async Task PollEncoderAsync(CancellationToken token)
    {
        var seq = Interlocked.Increment(ref _sendSeq);

        _logger.LogInformation(
            "[SEND S#{Seq:D5} {Ts:HH:mm:ss.fff}] command='PollEncoder' payload='{Payload}'",
            seq, DateTimeOffset.Now, Rotor.EncoderLocationA.TrimEnd('\r', '\n'));

        bool sent = await _tcp.SendCommandAsync(
            new TCPMessageBody<string>("ROTOR", "WRITE TEXT", Rotor.EncoderLocationA),
            token);

        if (!sent)
            _logger.LogWarning("[SEND S#{Seq:D5}] PollEncoder REJECTED by TCP layer", seq);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Encoder angle parsing
    // Receives the already-extracted 4-digit data field from the handler.
    // ─────────────────────────────────────────────────────────────────────────
    private void UpdateEncoderAngle(string dataField, long recvSeq)
    {
        try
        {
            if (!int.TryParse(dataField, out int encoder))
            {
                _logger.LogWarning(
                    "[RECV #{Seq:D5}] MRL data field '{DataField}' is not a valid integer — " +
                    "possible corrupt packet",
                    recvSeq, dataField);
                return;
            }

            double degrees = Math.Clamp((encoder - 5000) * 0.0879, 0, 180);

            _logger.LogInformation(
                "[RECV R#{Seq:D5}] Encoder count={Count} → {Degrees:F2}°",
                recvSeq, encoder, degrees);

            MainThread.BeginInvokeOnMainThread(() => ArmAngle = degrees);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "[RECV #{Seq:D5}] UpdateEncoderAngle threw for dataField='{DataField}'",
                recvSeq, dataField);
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Helpers
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Thread-safe status update marshalled to the UI thread.
    /// Callers do not need to wrap in BeginInvokeOnMainThread.
    /// </summary>
    private void SetStatus(string text)
    {
        if (MainThread.IsMainThread)
            StatusText = text;
        else
            MainThread.BeginInvokeOnMainThread(() => StatusText = text);
    }
}