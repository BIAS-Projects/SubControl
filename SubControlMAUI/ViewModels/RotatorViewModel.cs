namespace SubControlMAUI.ViewModels;


using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using Microsoft.Extensions.Logging;
using SubConsole.Models;
using SubControlMAUI.Messages;
using SubControlMAUI.Models;
using SubControlMAUI.Services;
using System.Diagnostics;

// ─────────────────────────────────────────────────────────────────────────────
// Gauge drawable (unchanged from original)
// ─────────────────────────────────────────────────────────────────────────────
public class RotatorGaugeDrawable : IDrawable
{
    public double ArmAngle { get; set; }
    public int MinValue { get; set; }
    public int MaxValue { get; set; }

    public void Draw(ICanvas canvas, RectF dirtyRect)
    {
        float cx = dirtyRect.Width / 2f;
        float cy = dirtyRect.Height - 10f;
        float radius = Math.Min(cx, cy) * 0.85f;

        var path = new PathF();
        path.MoveTo(cx - radius, cy);
        path.LineTo(cx + radius, cy);
        path.AddArc(cx - radius, cy - radius, cx + radius, cy + radius, 0f, 180f, false);
        path.Close();
        canvas.FillColor = Color.FromArgb("#1A2979FF");
        canvas.FillPath(path);

        int range = Math.Max(1, MaxValue - MinValue);
        double clamped = Math.Clamp(ArmAngle, MinValue, MaxValue);
        double fraction = (clamped - MinValue) / range;
        double needleCanvasDeg = 180.0 - (fraction * 180.0);
        double needleRad = needleCanvasDeg * Math.PI / 180.0;
        float nx = cx + radius * (float)Math.Cos(needleRad);
        float ny = cy - radius * (float)Math.Sin(needleRad);

        canvas.StrokeColor = Color.FromArgb("#FF1744");
        canvas.StrokeSize = 3f;
        canvas.StrokeLineCap = LineCap.Round;
        canvas.DrawLine(cx, cy, nx, ny);

        canvas.FillColor = Color.FromArgb("#FF1744");
        canvas.FillCircle(cx, cy, 7);
        canvas.FillColor = Color.FromArgb("#FFFFFF");
        canvas.FillCircle(cx, cy, 3);
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// RotatorViewModel
// ─────────────────────────────────────────────────────────────────────────────
public partial class RotatorViewModel : BaseViewModel
{
    // ── gauge ─────────────────────────────────────────────────────────────────
    public RotatorGaugeDrawable GaugeDrawable { get; } = new();

    private void RefreshGauge()
    {
        GaugeDrawable.ArmAngle = ArmAngle;
        GaugeDrawable.MinValue = MinRotatorValue;
        GaugeDrawable.MaxValue = MaxRotatorValue;
        OnPropertyChanged(nameof(GaugeDrawable));
    }

    // ── services ──────────────────────────────────────────────────────────────
    private readonly ILogger<RotatorViewModel> _logger;
    private readonly IMessenger _messenger;
    private readonly TcpSocketService _tcp;
    private readonly IAlertService _alertService;

    /// <summary>
    /// Shared dispatcher for request/response and push-confirm operations.
    /// The rotator exclusively uses push-confirm (SendAndWaitForPushAsync) because
    /// the device never echoes a CommandResponse JSON body.
    /// </summary>
    private readonly CommandDispatcherService _dispatcher;

    public ApplicationStateService AppState { get; }

    // ── command serialisation ─────────────────────────────────────────────────
    private readonly SemaphoreSlim _commandLock = new(1, 1);

    // ── polling ───────────────────────────────────────────────────────────────
    private CancellationTokenSource? _pollingCts;
    private readonly TimeSpan _pollingInterval = TimeSpan.FromMilliseconds(500);
    private bool _isPageVisible;

    // ── throttle ──────────────────────────────────────────────────────────────
    private long _lastStatusTick;
    private const long StatusThrottleMs = 100;

    // ── timeouts ──────────────────────────────────────────────────────────────
    private readonly TimeSpan _commandTimeout = TimeSpan.FromSeconds(10);
    private readonly TimeSpan _pollTimeout = TimeSpan.FromSeconds(1.5);
    private readonly TimeSpan _lockWaitTimeout = TimeSpan.FromSeconds(2);

    // ─────────────────────────────────────────────────────────────────────────
    // Constructor
    // ─────────────────────────────────────────────────────────────────────────
    public RotatorViewModel(
        IMessenger messenger,
        ILogger<RotatorViewModel> logger,
        TcpSocketService tcp,
        ApplicationStateService appState,
        IAlertService alertService,
        CommandDispatcherService dispatcher)
    {
        Title = "Rotator Control";
        _logger = logger;
        _messenger = messenger;
        AppState = appState;
        _tcp = tcp;
        _alertService = alertService;
        _dispatcher = dispatcher;
            _dispatcher.Owner = nameof(RotatorViewModel);

        ArmAngle = 73;
        //StatusText = string.Empty;

        //MinRotatorValue = Rotator.MinRotatorValue;
        //MaxRotatorValue = Rotator.MaxRotatorValue;
        //AdjustValue = Rotator.AdjustValue;

        //RefreshGauge();
        RegisterMessages();
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Message registration
    // ─────────────────────────────────────────────────────────────────────────
    private void RegisterMessages()
    {
        _messenger.Register<TcpDataReceivedMessage>(this, (_, msg) =>
        {
            // Let the dispatcher resolve any pending push-confirm first
            _dispatcher.HandleIncoming(
                msg.Value.Function,
                msg.Value.Command,
                msg.Value.Data);

            // Then do RotatorViewModel-specific processing (encoder angle updates etc.)
            _ = HandleTcpDataAsync(msg);
        });

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
    [ObservableProperty] private string statusText = string.Empty;

    [ObservableProperty] private double armAngle;
    partial void OnArmAngleChanged(double value)
    {
        Math.Clamp(value, 0, 180);
        RefreshGauge();
    }

    [ObservableProperty] private int minRotatorValue;
    partial void OnMinRotatorValueChanged(int value) => RefreshGauge();

    [ObservableProperty] private int maxRotatorValue;
    partial void OnMaxRotatorValueChanged(int value) => RefreshGauge();

    [ObservableProperty] private int adjustValue;

    public void UpdateRotatorSettings()
    {
        StatusText = string.Empty;

        MinRotatorValue = Rotator.MinRotatorValue;
        MaxRotatorValue = Rotator.MaxRotatorValue;
        AdjustValue = Rotator.AdjustValue;

        RefreshGauge();
    }
    // ─────────────────────────────────────────────────────────────────────────
    // Commands
    // ─────────────────────────────────────────────────────────────────────────

    [RelayCommand]
    private Task Park()
        => RunCommandAsync(
            "Parking rotator...", "Rotator parking...", "Park rotator command failed",
            Rotator.GenerateParkOrDeployCommandString(true),
            data => MatchesCommandCode(data, "MML"));

    [RelayCommand]
    private Task Deploy()
        => RunCommandAsync(
            "Deploying rotator...", "Rotator deploying...", "Deploy rotator command failed",
            Rotator.GenerateParkOrDeployCommandString(false),
            data => MatchesCommandCode(data, "MML"));

    [RelayCommand]
    private Task Forward()
        => RunCommandAsync(
            "Driving rotator forward...", "Rotator forward command confirmed", "Forward rotator command failed",
            Rotator.PanMotorAForward,
            data => MatchesCommandCode(data, "MMF"));

    [RelayCommand]
    private Task Backward()
        => RunCommandAsync(
            "Driving rotator backward...", "Rotator backward command confirmed", "Backward rotator command failed",
            Rotator.PanMotorABackward,
            data => MatchesCommandCode(data, "MMB"));

    [RelayCommand]
    private Task Stop()
        => RunCommandAsync(
            "Stopping rotator...", "Rotator stop command confirmed", "Stop rotator command failed",
            Rotator.StopPanMotorA,
            data => MatchesCommandCode(data, "MST"));

    [RelayCommand]
    private Task AdjustBackward()
        => RunCommandAsync(
            "Adjusting rotator backwards...", "Rotator moving...", "Adjust backward command failed",
            Rotator.GenerateNudgeCommandString(true, ArmAngle),
            data => MatchesCommandCode(data, "MML"));

    [RelayCommand]
    private Task AdjustForward()
        => RunCommandAsync(
            "Adjusting rotator forwards...", "Rotator moving...", "Adjust forward command failed",
            Rotator.GenerateNudgeCommandString(false, ArmAngle),
            data => MatchesCommandCode(data, "MML"));

    [RelayCommand]
    private async Task GoBack() => await Shell.Current.GoToAsync("..");

    // ─────────────────────────────────────────────────────────────────────────
    // Shared command runner
    //
    // All rotator commands are push-confirm: we write a string over the rotator
    // serial port and wait for the device to push back a response on the same
    // channel that satisfies confirmPredicate.
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

            bool ok = await _dispatcher.SendAndWaitForPushAsync(
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
    // TCP data handler — encoder angle processing
    //
    // The dispatcher already resolved any pending push-confirm before this
    // method is called.  Here we only extract the encoder position for the
    // gauge needle; we don't need to touch the dispatcher again.
    // ─────────────────────────────────────────────────────────────────────────
    private Task HandleTcpDataAsync(TcpDataReceivedMessage msg)
    {
        try
        {
            var function = msg.Value.Function;
            var raw = msg.Value.Data;

            if (!function.Equals(Feature.RotatorName, StringComparison.OrdinalIgnoreCase))
                return Task.CompletedTask;

            Debug.WriteLine($"RotatorViewModel raw incoming: '{raw}'");

            var data = raw?.Trim();
            if (string.IsNullOrWhiteSpace(data)) return Task.CompletedTask;

            bool hasValidHeader = Rotator.Headers.Any(h => data.StartsWith(h, StringComparison.OrdinalIgnoreCase));
            bool hasValidTerminator = Rotator.Terminators.Any(t => data.EndsWith(t, StringComparison.OrdinalIgnoreCase));

            if (!hasValidHeader || !hasValidTerminator) return Task.CompletedTask;

            long now = Environment.TickCount64;
            if ((now - Interlocked.Read(ref _lastStatusTick)) >= StatusThrottleMs)
                Interlocked.Exchange(ref _lastStatusTick, now);

            if (MatchesCommandCode(data, "MRL"))
                UpdateEncoderAngle(data);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "HandleTcpDataAsync threw unexpectedly");
        }

        return Task.CompletedTask;
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
            // Encoder poll uses the dispatcher directly; the response will also
            // come through HandleTcpDataAsync which calls UpdateEncoderAngle.
            await _dispatcher.SendAndWaitForPushAsync(
                Feature.RotatorName, "WRITE TEXT", Rotator.EncoderLocationA,
                Feature.RotatorName, data => MatchesCommandCode(data, "MRL"),
                _pollTimeout);
        }
        catch (OperationCanceledException) { }
        finally { _commandLock.Release(); }
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Encoder angle parsing
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
                StatusText = "Rotator reported a non-numeric position";
                return;
            }

            double degrees = Math.Clamp((encoder - 5000) * 0.0879, 0, 180);
            if (degrees > MaxRotatorValue || encoder < MinRotatorValue)
            {
                StatusText = $"Rotator position reported as {encoder} — " +
                             $"min {MinRotatorValue}, max {MaxRotatorValue}";
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
    // Helpers
    // ─────────────────────────────────────────────────────────────────────────
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


    // In RotatorViewModel — public so MainViewModel can call it during system enable
    public async Task<bool> EnableRotatorAsync()
    {
        if (!await _commandLock.WaitAsync(_lockWaitTimeout))
        {
            SetStatus("Command already in progress");
            return false;
        }
        try
        {
            return await EnableRotatorInternalAsync();
        }
        finally
        {
            _commandLock.Release();
        }
    }

    private async Task<bool> EnableRotatorInternalAsync()
    {
        if (!await _dispatcher.SendAndWaitForPushAsync(
                Feature.RotatorName, "WRITE TEXT",
                Rotator.GenerateSetSpeedCommandString(),
                Feature.RotatorName,
                response => response.Contains("MSP"),
                _commandTimeout))
        {
            SetStatus("Rotator failed to respond to set speed command");
            return false;
        }

        if (!await _dispatcher.SendAndWaitForPushAsync(
                Feature.RotatorName, "WRITE TEXT",
                Rotator.GetFirmwareVersion,
                Feature.RotatorName,
                response => response.Contains("MRV"),
                _commandTimeout))
        {
            SetStatus("Rotator failed to respond to firmware version request");
            return false;
        }

        return true;
    }
}