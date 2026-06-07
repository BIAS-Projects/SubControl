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

// ─────────────────────────────────────────────────────────────────────────────
// Gauge drawable – draws a semi-circular arc with a needle.
// Implements IDrawable so it can be used directly as GraphicsView.Drawable.
// ─────────────────────────────────────────────────────────────────────────────
public class RotatorGaugeDrawable : IDrawable
{
    public double ArmAngle { get; set; }   // 0–180 degrees, maps directly to needle
    public int MinValue { get; set; }
    public int MaxValue { get; set; }

    public void Draw(ICanvas canvas, RectF dirtyRect)
    {
        float cx = dirtyRect.Width / 2f;
        float cy = dirtyRect.Height - 10f;   // pivot at bottom-centre
        float radius = Math.Min(cx, cy) * 0.85f;
        float thick = 18f;

        // ── 1. Solid filled semicircle background (flat edge at bottom) ──────
        var path = new PathF();
        path.MoveTo(cx - radius, cy);
        path.LineTo(cx + radius, cy);
        // Arc from 0° (right) back round to 180° (left) — top half
        path.AddArc(cx - radius, cy - radius,
                    cx + radius, cy + radius,
                    0f, 180f, false);
        path.Close();
        canvas.FillColor = Color.FromArgb("#1A2979FF");   // translucent blue tint
        canvas.FillPath(path);


        // ── 4. Needle — angle driven by ArmAngle mapped within Min/Max range
        // MinValue → needle points left  (canvas angle 180°)
        // MaxValue → needle points right (canvas angle 0°)
        int range = Math.Max(1, MaxValue - MinValue);
        double clamped = Math.Clamp(ArmAngle, MinValue, MaxValue);
        double fraction = (clamped - MinValue) / range;        // 0.0 → 1.0
        double needleCanvasDeg = 180.0 - (fraction * 180.0);        // 180° (left) → 0° (right)
        double needleRad = needleCanvasDeg * Math.PI / 180.0;
        float nx = cx + radius * (float)Math.Cos(needleRad);
        float ny = cy - radius * (float)Math.Sin(needleRad);

        // Needle itself
        canvas.StrokeColor = Color.FromArgb("#FF1744");
        canvas.StrokeSize = 3f;
        canvas.StrokeLineCap = LineCap.Round;
        canvas.DrawLine(cx, cy, nx, ny);

        // ── 5. Centre pivot dot ──────────────────────────────────────────────
        canvas.FillColor = Color.FromArgb("#FF1744");
        canvas.FillCircle(cx, cy, 7);
        canvas.FillColor = Color.FromArgb("#FFFFFF");
        canvas.FillCircle(cx, cy, 3);
    }
}

public partial class RotatorViewModel : BaseViewModel
{
    // ── gauge drawable (updated whenever angle or limits change) ──────────────
    public RotatorGaugeDrawable GaugeDrawable { get; } = new();

    private void RefreshGauge()
    {
        GaugeDrawable.ArmAngle = ArmAngle;
        GaugeDrawable.MinValue = MinRotatorValue;
        GaugeDrawable.MaxValue = MaxRotatorValue;
        OnPropertyChanged(nameof(GaugeDrawable));   // triggers GraphicsView redraw
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Immutable holder for a single pending push-confirm operation.
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
    private readonly SemaphoreSlim _commandLock = new(1, 1);
    private PendingConfirm? _pendingConfirm;

    // ── polling ───────────────────────────────────────────────────────────────
    private CancellationTokenSource? _pollingCts;
    private readonly TimeSpan _pollingInterval = TimeSpan.FromMilliseconds(500);
    private bool _isPageVisible;

    // ── UI-update throttle ────────────────────────────────────────────────────
    private long _lastStatusTick;
    private const long StatusThrottleMs = 100;

    // ── protocol timeouts ─────────────────────────────────────────────────────
    private readonly TimeSpan _commandTimeout = TimeSpan.FromSeconds(10);
    private readonly TimeSpan _pollTimeout = TimeSpan.FromSeconds(1.5);
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

        RefreshGauge();
        RegisterMessages();
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Message registration (unchanged)
    // ─────────────────────────────────────────────────────────────────────────
    private void RegisterMessages()
    {
        _messenger.Register<TcpDataReceivedMessage>(this, (_, msg) =>
            _ = HandleTcpDataAsync(msg));

        _messenger.Register<TcpSendRequestMessage>(this, (_, _) => { });

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
    // Observable properties
    // ─────────────────────────────────────────────────────────────────────────
    [ObservableProperty] private double buttonSize;
    [ObservableProperty] private double layoutSpacing;
    [ObservableProperty] private string statusText = "Stopped";

    [ObservableProperty] private double armAngle;
    partial void OnArmAngleChanged(double value)
    {
        value = Math.Clamp(value, 0, 180);
        RefreshGauge();
    }

    [ObservableProperty] private int minRotatorValue = 0;
    partial void OnMinRotatorValueChanged(int value) => RefreshGauge();

    [ObservableProperty] private int maxRotatorValue = 90;
    partial void OnMaxRotatorValueChanged(int value) => RefreshGauge();

    [ObservableProperty] private int adjustValue = 1;

    // ─────────────────────────────────────────────────────────────────────────
    // Existing commands
    // ─────────────────────────────────────────────────────────────────────────
    [RelayCommand]
    private async Task Park()
        => await RunCommandAsync(
               "Parking rotator...",
               "Rotator parking...",
               "Park rotator command failed",
               Rotator.GenerateParkOrDeployCommandString(true),
               data => MatchesCommandCode(data, "MML"));

    [RelayCommand]
    private async Task Deploy()
        => await RunCommandAsync(
               "Deploying rotator...",
               "Rotator deploying...",
               "Deploy rotator command failed",
               Rotator.GenerateParkOrDeployCommandString(false),
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
    // New adjust commands (dummy implementations – wire up to hardware later)
    // ─────────────────────────────────────────────────────────────────────────
    [RelayCommand]
    private async Task AdjustBackward()
        => await RunCommandAsync(
               "Adjusting rotator backwards...",
               "Rotator moving...",
               "Park rotator command failed",
               Rotator.GenerateNudgeCommandString(true, ArmAngle),
               data => MatchesCommandCode(data, "MML"));
    

    [RelayCommand]
    private async Task AdjustForward()
        => await RunCommandAsync(
               "Adjusting rotator forwards...",
               "Rotator moving...",
               "Park rotator command failed",
               Rotator.GenerateNudgeCommandString(false, ArmAngle),
               data => MatchesCommandCode(data, "MML"));

    [RelayCommand]
    private async Task GoBack()
    {
        await Shell.Current.GoToAsync("..");
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Shared command runner (unchanged)
    // ─────────────────────────────────────────────────────────────────────────
    private async Task RunCommandAsync(
        string pendingMessage,
        string successMessage,
        string failureMessage,
        string payload,
        Func<string, bool> confirmPredicate)
    {
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

    private async Task<bool> SendAndWaitForConfirmAsync(
        string sendFeature, string sendCommand, string sendData,
        string confirmFunction, Func<string, bool> confirmPredicate,
        TimeSpan timeout)
    {
        var op = new PendingConfirm(confirmFunction, confirmPredicate);
        Interlocked.Exchange(ref _pendingConfirm, op);
        try
        {
            bool sent = await _tcp.SendCommandAsync(
                new TCPMessageBody<string>(sendFeature, sendCommand, sendData),
                CancellationToken.None);
            if (!sent) return false;

            var completed = await Task.WhenAny(op.Tcs.Task, Task.Delay(timeout));
            return completed == op.Tcs.Task && op.Tcs.Task.Result;
        }
        finally
        {
            Interlocked.CompareExchange(ref _pendingConfirm, null, op);
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    // TCP data handler (unchanged)
    // ─────────────────────────────────────────────────────────────────────────
    private Task HandleTcpDataAsync(TcpDataReceivedMessage msg)
    {
        try
        {
            var function = msg.Value.Function;
            var raw = msg.Value.Data;

            if (!function.Equals(Feature.RotatorName, StringComparison.OrdinalIgnoreCase))
                return Task.CompletedTask;

            var data = raw?.Trim();
            if (string.IsNullOrWhiteSpace(data)) return Task.CompletedTask;

            bool hasValidHeader = Rotator.Headers.Any(h => data.StartsWith(h, StringComparison.OrdinalIgnoreCase));
            bool hasValidTerminator = Rotator.Terminators.Any(t => data.EndsWith(t, StringComparison.OrdinalIgnoreCase));

            if (!hasValidHeader || !hasValidTerminator) return Task.CompletedTask;

            long now = Environment.TickCount64;
            if ((now - Interlocked.Read(ref _lastStatusTick)) >= StatusThrottleMs)
            {
                Interlocked.Exchange(ref _lastStatusTick, now);
            //    SetStatus(data);
            }

            if (MatchesCommandCode(data, "MRL"))
                UpdateEncoderAngle(data);

            var pending = Interlocked.CompareExchange(ref _pendingConfirm, null, null);
            if (pending is not null
                && function.Equals(pending.Function, StringComparison.OrdinalIgnoreCase)
                && pending.Predicate(data))
            {
                pending.Tcs.TrySetResult(true);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "HandleTcpDataAsync threw unexpectedly");
        }

        return Task.CompletedTask;
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Polling (unchanged)
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
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Rotator polling failed");
                SetStatus(ex.Message);
            }

            try { await Task.Delay(_pollingInterval, token); }
            catch (OperationCanceledException) { break; }
        }
    }

    private async Task PollEncoderAsync(CancellationToken token)
    {
        if (!await _commandLock.WaitAsync(TimeSpan.Zero, token)) return;
        try
        {
            var op = new PendingConfirm(Feature.RotatorName, data => MatchesCommandCode(data, "MRL"));
            Interlocked.Exchange(ref _pendingConfirm, op);
            try
            {
                bool sent = await _tcp.SendCommandAsync(
                    new TCPMessageBody<string>(Feature.RotatorName, "WRITE TEXT", Rotator.EncoderLocationA),
                    token);
                if (!sent) return;

                var completed = await Task.WhenAny(op.Tcs.Task, Task.Delay(_pollTimeout, token));
                if (completed != op.Tcs.Task)
                    _logger.LogWarning("Encoder poll timed out");
            }
            finally
            {
                Interlocked.CompareExchange(ref _pendingConfirm, null, op);
            }
        }
        catch (OperationCanceledException) { }
        finally { _commandLock.Release(); }
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Encoder angle parsing (unchanged)
    // ─────────────────────────────────────────────────────────────────────────
    private void UpdateEncoderAngle(string response)
    {
        try
        {
            response = response.Trim();
            if (response.Length < 10) return;
            if (!response.Substring(2, 3).Equals("MRL", StringComparison.OrdinalIgnoreCase)) return;
            if (!int.TryParse(response.Substring(5, 4), out int encoder))
            {
                StatusText = $"Rotator reported a non numeric position";
                return;
            }


            double degrees = Math.Clamp((encoder - 5000) * 0.0879, 0, 180);
            if (degrees > maxRotatorValue || encoder < minRotatorValue)
            {
                StatusText = $"Rotator position reported as {encoder} minimum value is {minRotatorValue} maximum value is {maxRotatorValue}";
                return;
            }

            OnArmAngleChanged(degrees);
            MainThread.BeginInvokeOnMainThread(() => ArmAngle = degrees);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed parsing encoder response '{Response}'", response);
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Helpers (unchanged)
    // ─────────────────────────────────────────────────────────────────────────
    private static bool MatchesResponse(string received, string expected)
        => received.Trim().Equals(expected.TrimEnd('\r', '\n'), StringComparison.Ordinal);

    private static bool MatchesCommand(string received, string command)
        => received.Contains(command, StringComparison.OrdinalIgnoreCase);

    private static bool MatchesCommandCode(string received, string code)
    {
        received = received.Trim();
        return received.Length >= 10
            && received.Substring(2, 3).Equals(code, StringComparison.OrdinalIgnoreCase);
    }

    private void SetStatus(string text)
    {
        if (MainThread.IsMainThread) StatusText = text;
        else MainThread.BeginInvokeOnMainThread(() => StatusText = text);
    }
}