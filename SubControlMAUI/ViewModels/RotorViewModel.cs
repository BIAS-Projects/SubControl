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

public partial class RotorViewModel : BaseViewModel
{

    private readonly ILogger<RotorViewModel> _loggerService;
    private readonly IMessenger _messengerService;
    private readonly TcpSocketService _tcpService;
    private readonly IAlertService _alertService;

    public ApplicationStateService AppState { get; }

    private TaskCompletionSource<bool>? _pendingCommand;
    private string? _pendingCommandName;

    // ── fields — all together at the top ─────────────────────────────────────
    private readonly SemaphoreSlim _commandLock = new(1, 1);
    private TaskCompletionSource<bool>? _pendingPushConfirm;
    private string? _pendingPushConfirmFunction;
    private Func<string, bool>? _pendingPushConfirmPredicate;
    private Guid _pendingPushConfirmId;

    private TaskCompletionSource<string>? _pendingResponse;
    private string? _pendingResponseFunction;
    private Func<string, bool>? _pendingResponsePredicate;
    private Guid _pendingResponseId;



    private TimeSpan timeout = TimeSpan.FromSeconds(10);


    private CancellationTokenSource? _pollingCts;

    private readonly TimeSpan _pollingInterval =
        TimeSpan.FromSeconds(2);

    private bool _isPageVisible;

    public RotorViewModel(
        IMessenger messengerService,
        ILogger<RotorViewModel> loggerService,
        TcpSocketService tcpService,
        ApplicationStateService applicationStateService,
        IAlertService alertService
        ) 
    {
        Title = "Rotor Control";
        _loggerService = loggerService;
        _messengerService = messengerService;
        AppState = applicationStateService;
        _tcpService = tcpService;
        _alertService = alertService;
        ArmAngle = 73;


       
        StatusText = "";

        //    int counter = 0;


        _messengerService.Register<TcpDataReceivedMessage>(this, async (r, msg) =>
        {
            if (!msg.Value.Function.Equals("ROTOR", StringComparison.OrdinalIgnoreCase))
                return;

            var data = msg.Value.Data?.Trim();

            StatusText = data ?? "";

            if (string.IsNullOrWhiteSpace(data))
                return;

            // =========================================================
            // HEADER CHECK (Rotor.Headers)
            // =========================================================

            bool hasValidHeader = Rotor.Headers.Any(h =>
                data.StartsWith(h, StringComparison.OrdinalIgnoreCase));

            if (!hasValidHeader)
                return;

            // =========================================================
            // TERMINATOR CHECK (Rotor.Terminators)
            // =========================================================

            bool hasValidTerminator = Rotor.Terminators.Any(t =>
                data.EndsWith(t, StringComparison.OrdinalIgnoreCase));

            if (!hasValidTerminator)
                return;

            // =========================================================
            // PUSH CONFIRM MATCH
            // =========================================================

            if (_pendingPushConfirm is { } confirmTcs
                && _pendingPushConfirmId != Guid.Empty
                && msg.Value.Function.Equals(_pendingPushConfirmFunction, StringComparison.OrdinalIgnoreCase)
                && _pendingPushConfirmPredicate?.Invoke(data) == true)
            {
                confirmTcs.TrySetResult(true);
            }

            // =========================================================
            // RESPONSE MATCH
            // =========================================================

            if (_pendingResponse is { } responseTcs
                && _pendingResponseId != Guid.Empty
                && msg.Value.Function.Equals(_pendingResponseFunction, StringComparison.OrdinalIgnoreCase)
                && _pendingResponsePredicate?.Invoke(data) == true)
            {
                responseTcs.TrySetResult(data);
            }
        });

        //_messengerService.Register<TcpDataReceivedMessage>(this, async (r, msg) =>
        //{
        //    if (!msg.Value.Function.Equals("ROTOR")) return;

        //    StatusText = $"{msg.Value.Data}";

        //    var data = msg.Value.Data?.Trim();

        //    if (string.IsNullOrWhiteSpace(data)
        //        || !data.StartsWith("#", StringComparison.OrdinalIgnoreCase)
        //        || !data.EndsWith("W", StringComparison.OrdinalIgnoreCase))
        //        return;

        //    if (_pendingPushConfirm is { } confirmTcs
        //        && _pendingPushConfirmId != Guid.Empty
        //        && msg.Value.Function == _pendingPushConfirmFunction
        //        && _pendingPushConfirmPredicate?.Invoke(data) == true)
        //    {
        //        confirmTcs.TrySetResult(true);
        //    }
        //    if (_pendingResponse is { } responseTcs
        //        && _pendingResponseId != Guid.Empty
        //        && msg.Value.Function == _pendingResponseFunction
        //        && _pendingResponsePredicate?.Invoke(data) == true)
        //    {
        //        responseTcs.TrySetResult(data);
        //    }
        //});

        _messengerService.Register<TcpSendRequestMessage>(this, (r, msg) =>
        {
            MainThread.BeginInvokeOnMainThread(() =>
            {
                //      Status = Encoding.UTF8.GetString(msg.Value);
            });

        });

        _messengerService.Register<TcpStatusMessage>(this, (r, msg) =>
        {
            MainThread.BeginInvokeOnMainThread(() =>
            {
                StatusText = msg.Value;
            });

        });

        _messengerService.Register<TcpErrorMessage>(this, (r, msg) =>
        {
            _loggerService?.LogError($"TcpErrorMessage : {msg}", msg);
            MainThread.BeginInvokeOnMainThread(() =>
            {
                StatusText = msg.Value.Message;

            });

        });



        _messengerService.Register<TcpAckTimeoutMessage>(this, (r, msg) =>
        {

            MainThread.BeginInvokeOnMainThread(() =>
                StatusText = $"No response to: {msg.Command}");
        });

        _messengerService.Register<TcpNackMessage>(this, (r, msg) =>
        {
            MainThread.BeginInvokeOnMainThread(() =>
                StatusText = $"Server rejected '{msg.Command}': {msg.Reason}");
        });


        _messengerService.Register<TcpIsConnected>(this, (r, msg) =>
        {
            MainThread.BeginInvokeOnMainThread(async () =>
            {
                if (!msg.Value)
                    await Shell.Current.GoToAsync("//MainPage");
            });
        });


    }

    [ObservableProperty]
    private double buttonSize;

    [ObservableProperty]
    private double layoutSpacing;


    [ObservableProperty]
    private string statusText = "Stopped";

    // =====================================================
    // ANGLE
    // =====================================================

    [ObservableProperty]
    private double armAngle;



    partial void OnArmAngleChanged(double value)
    {
        value = Math.Clamp(value, 0, 180);

        // TODO:
        // Send command to hardware here

        // Example:
        //
        // Send($"ROTOR ANGLE {(int)value}");
    }

    // =====================================================
    // COMMANDS
    // =====================================================

    [RelayCommand]
    private async Task Park()
    {
        // Cancel any in-progress poll to free the lock quickly
        _pollInterruptCts?.Cancel();

        // Now wait briefly for the lock (poll should release almost immediately)
        if (!await _commandLock.WaitAsync(TimeSpan.FromSeconds(2)))
        {
            StatusText = "Command already in progress";
            return;
        }

        IsBusy = true;

        try
        {

            //        var sent = await _tcpService.SendCommandAsync(
            //new TCPMessageBody<string>("ROTOR", "WRITE TEXT", Rotor.ParkMotorA), CancellationToken.None);

            StatusText = "Parking rotor...";
            if (!await SendAndWaitForPushAsyncInner(
                    "ROTOR", "WRITE TEXT", Rotor.ParkMotorA,
                    "ROTOR",
                    data => MatchesResponse(data, Rotor.ParkMotorA),
                    timeout))
            {
                StatusText = "Park rotor command failed";
                return;
            }
            StatusText = "Rotor parked command confirmed";
        }
        finally
        {

            IsBusy = false;
            _commandLock.Release();
        }
    }

    [RelayCommand]
    private async Task Deploy()
    {
        // Cancel any in-progress poll to free the lock quickly
        _pollInterruptCts?.Cancel();

        // Now wait briefly for the lock (poll should release almost immediately)
        if (!await _commandLock.WaitAsync(TimeSpan.FromSeconds(2)))
        {
            StatusText = "Command already in progress";
            return;
        }

        IsBusy = true;
        try
        {
            StatusText = "Deploying rotor...";
            if (!await SendAndWaitForPushAsyncInner(
                    "ROTOR", "WRITE TEXT", Rotor.DeployMotorA,
                    "ROTOR",
                    data => MatchesResponse(data, Rotor.DeployMotorA),
                    timeout))
            {
                StatusText = "Deploy rotor command failed";
                return;
            }
            StatusText = "Rotor deploy command confirmed";
        }
        finally
        {
            IsBusy = false;
            _commandLock.Release();
        }
    }


    [RelayCommand]
    private async Task Stop()
    {
        _pollInterruptCts?.Cancel();

        if (!await _commandLock.WaitAsync(TimeSpan.FromSeconds(2)))
        {
            StatusText = "Command already in progress";
            return;
        }

        IsBusy = true;
        try
        {
            StatusText = "Stopping rotor...";  // also fix the copy-paste "Deploying" message
            if (!await SendAndWaitForPushAsyncInner(
                    "ROTOR", "WRITE TEXT", Rotor.StopPanMotorA,
                    "ROTOR",
                    data => MatchesCommand(data, "MST"),  // <-- match on command code, not full string
                    timeout))
            {
                StatusText = "Stop rotor command failed";
                return;
            }
            StatusText = "Rotor stop command confirmed";
        }
        finally
        {
            IsBusy = false;
            _commandLock.Release();
        }
    }


    // ── helpers ────────────────────────────────────────────────────────────────
    private static bool MatchesResponse(string received, string expected)
        => received.Trim() == expected.TrimEnd('\r', '\n');

    private static bool MatchesCommand(string received, string expected)
    => received.Contains(expected);


    private async Task<bool> SendAndWaitForPushAsync(
        string sendFeature, string sendCommand, string sendData,
        string confirmFunction, Func<string, bool> confirmPredicate,
        TimeSpan timeout)
    {
        await _commandLock.WaitAsync();
        try
        {
            var operationId = Guid.NewGuid();
            _pendingPushConfirmFunction = confirmFunction;
            _pendingPushConfirmPredicate = confirmPredicate;
            _pendingPushConfirmId = operationId;

            var confirmTcs = new TaskCompletionSource<bool>();
            Interlocked.Exchange(ref _pendingPushConfirm, confirmTcs);

            var sent = await _tcpService.SendCommandAsync(
                new TCPMessageBody<string>(sendFeature, sendCommand, sendData),
                CancellationToken.None);

            if (!sent)
            {
                Interlocked.Exchange(ref _pendingPushConfirm, null);
                _pendingPushConfirmFunction = null;
                _pendingPushConfirmPredicate = null;
                _pendingPushConfirmId = Guid.Empty;
                return false;
            }

            var completed = await Task.WhenAny(confirmTcs.Task, Task.Delay(timeout));

            Interlocked.Exchange(ref _pendingPushConfirm, null);
            _pendingPushConfirmFunction = null;
            _pendingPushConfirmPredicate = null;
            _pendingPushConfirmId = Guid.Empty;

            return completed == confirmTcs.Task && confirmTcs.Task.Result;
        }
        finally
        {
            _commandLock.Release();
        }
    }


    private async Task<bool> SendAndWaitForPushAsyncInner(
    string sendFeature, string sendCommand, string sendData,
    string confirmFunction, Func<string, bool> confirmPredicate,
    TimeSpan timeout)
    {
        var operationId = Guid.NewGuid();
        _pendingPushConfirmFunction = confirmFunction;
        _pendingPushConfirmPredicate = confirmPredicate;
        _pendingPushConfirmId = operationId;

        var confirmTcs = new TaskCompletionSource<bool>();
        Interlocked.Exchange(ref _pendingPushConfirm, confirmTcs);

        var sent = await _tcpService.SendCommandAsync(
            new TCPMessageBody<string>(sendFeature, sendCommand, sendData),
            CancellationToken.None);

        if (!sent)
        {
            Interlocked.Exchange(ref _pendingPushConfirm, null);
            _pendingPushConfirmFunction = null;
            _pendingPushConfirmPredicate = null;
            _pendingPushConfirmId = Guid.Empty;
            return false;
        }

        var completed = await Task.WhenAny(confirmTcs.Task, Task.Delay(timeout));

        Interlocked.Exchange(ref _pendingPushConfirm, null);
        _pendingPushConfirmFunction = null;
        _pendingPushConfirmPredicate = null;
        _pendingPushConfirmId = Guid.Empty;

        return completed == confirmTcs.Task && confirmTcs.Task.Result;
    }

    private async Task<bool> SendAndWaitAsync(
 string feature, string command, string data, TimeSpan timeout)
    {
        _pendingCommandName = command;
        var tcs = new TaskCompletionSource<bool>();
        Interlocked.Exchange(ref _pendingCommand, tcs);  // atomic set

        var sent = await _tcpService.SendCommandAsync(
            new TCPMessageBody<string>(feature, command, data), CancellationToken.None);

        if (!sent)
        {
            Interlocked.Exchange(ref _pendingCommand, null);  // atomic clear
            _pendingCommandName = null;
            return false;
        }

        var completed = await Task.WhenAny(tcs.Task, Task.Delay(timeout));

        Interlocked.Exchange(ref _pendingCommand, null);  // atomic clear
        _pendingCommandName = null;

        return completed == tcs.Task && tcs.Task.Result;
    }

 //   private readonly SemaphoreSlim _commandLock = new(1, 1);



    private async Task ResolvePendingCommandAsync(string? json)
    {
        // Capture atomically — local copy is thread-safe from this point on
        var pending = Interlocked.CompareExchange(ref _pendingCommand, null, null);
        if (pending is null) return;

        try
        {
            var response = JsonSerializer.Deserialize<CommandResponse>(
                json ?? "",
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

            pending.TrySetResult(response?.Ok == true);
        }
        catch
        {
            pending.TrySetResult(false);
        }
    }


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
    private void CleanupPendingResponse()
    {
        Interlocked.Exchange(ref _pendingResponse, null);

        _pendingResponseFunction = null;
        _pendingResponsePredicate = null;
        _pendingResponseId = Guid.Empty;
    }

    //Stopped with cancellation token in OnDisappearing
    private async Task StartPollingAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            try
            {
                if (_isPageVisible && !IsBusy)
                {
                    await PollEncoderAsync(token);
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _loggerService.LogError(ex,
                    "Rotor polling failed");

                StatusText = ex.Message;
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

    private CancellationTokenSource? _pollInterruptCts;

    private async Task PollEncoderAsync(CancellationToken token)
    {
        if (!await _commandLock.WaitAsync(0))
            return;

        _pollInterruptCts = new CancellationTokenSource();
        IsBusy = true;
        try
        {
            var response = await SendAndWaitForResponseAsync(
                "ROTOR", "WRITE TEXT", Rotor.EncoderLocationA,
                "ROTOR",
                data => MatchesCommand(data, "MRL"),
                timeout,
                _pollInterruptCts.Token);  // <-- pass token here

            if (string.IsNullOrWhiteSpace(response))
                StatusText = "Encoder timeout";
            else
                UpdateEncoderAngle(response);
        }
        finally
        {
            _pollInterruptCts = null;
            IsBusy = false;
            _commandLock.Release();
        }
    }

    private void UpdateEncoderAngle(string response)
    {
        try
        {
            // Expected:
            // #AMRL6274R

            response = response.Trim();

            if (response.Length < 10)
                return;

            string command = response.Substring(2, 3);

            if (!command.Equals(
                    "MRL",
                    StringComparison.OrdinalIgnoreCase))
                return;

            string digits = response.Substring(5, 4);

            if (!int.TryParse(digits, out int encoder))
                return;

            double degrees =
                (encoder - 5000) * 0.0879;

            degrees = Math.Clamp(degrees, 0, 180);

            MainThread.BeginInvokeOnMainThread(() =>
            {
                ArmAngle = degrees;

                StatusText =
                    $"Rotor position: {degrees:F1}°";
            });
        }
        catch (Exception ex)
        {
            _loggerService.LogWarning(ex,
                "Failed parsing encoder response '{Response}'",
                response);
        }
    }

    private async Task<string?> SendAndWaitForResponseAsync(
        string sendFeature,
        string sendCommand,
        string sendData,
        string responseFunction,
        Func<string, bool> responsePredicate,
        TimeSpan timeout,
        CancellationToken cancellationToken = default)  // <-- add this
    {
        var operationId = Guid.NewGuid();

        _pendingResponseFunction = responseFunction;
        _pendingResponsePredicate = responsePredicate;
        _pendingResponseId = operationId;

        var responseTcs = new TaskCompletionSource<string>();
        Interlocked.Exchange(ref _pendingResponse, responseTcs);

        var sent = await _tcpService.SendCommandAsync(
            new TCPMessageBody<string>(sendFeature, sendCommand, sendData),
            CancellationToken.None);

        if (!sent)
        {
            CleanupPendingResponse();
            return null;
        }

        try
        {
            var completed = await Task.WhenAny(
                responseTcs.Task,
                Task.Delay(timeout, cancellationToken));

            CleanupPendingResponse();

            if (completed != responseTcs.Task)
                return null;

            return await responseTcs.Task;
        }
        catch (OperationCanceledException)
        {
            CleanupPendingResponse();
            return null;
        }
    }
        //private static bool MatchesResponse(string received, string expected)
        //=> received.Trim() == expected.TrimEnd('\r', '\n');
    }