
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using Microsoft.Extensions.Logging;
using SubConsole.Models;
using SubControlMAUI.Messages;
using SubControlMAUI.Models;
using SubControlMAUI.Pages;
using SubControlMAUI.Services;
using System.Text;
using System.Text.Json;

namespace SubControlMAUI.ViewModels;

public partial class MainViewModel : BaseViewModel
{

    SQLiteService _sqliteService;
    IAlertService _alertService;
    INavigationService _navigationService;
    ILogger<MainViewModel> _loggerService;
    private readonly IMessenger _messengerService;
    private readonly TcpSocketService _tcpService;

    private TaskCompletionSource<bool>? _pendingCommand;
    private string? _pendingCommandName;

    //private TaskCompletionSource<bool>? _pendingPushConfirm;
    //private string? _pendingPushConfirmValue;
    //private string? _pendingPushConfirmFunction;

    private TaskCompletionSource<bool>? _pendingPushConfirm;
    private string? _pendingPushConfirmFunction;
    private Func<string, bool>? _pendingPushConfirmPredicate;

    private static string SerialFeature => nameof(FeatureOptionViewModel);
    private static string CameraFeature => nameof(FeatureOptionViewModel) + "CAMERA";

    private TimeSpan timeout = TimeSpan.FromSeconds(10);


    public MainViewModel(SQLiteService sqliteService,
        IAlertService alertService,
        IMessenger messengerService,
        TcpSocketService tcpService,
        ILogger<MainViewModel> loggerService,
        INavigationService navigationService)
    {
        Title = "Main Menu";
        StatusText = "Disconnected";
        _sqliteService = sqliteService;
        _alertService = alertService;
        _tcpService = tcpService;
        _navigationService = navigationService;
        _messengerService = messengerService;
        _loggerService = loggerService;


        _messengerService.Register<TcpDataReceivedMessage>(this, async (r, msg) =>
        {
            // Serial feature responses (OPEN commands)
            if (msg.Value.Function.Equals(SerialFeature))
            {
                if (msg.Value.Command == _pendingCommandName)
                    await ResolvePendingCommandAsync(msg.Value.Data);
                return;
            }

            // Camera feature responses (CHECK FFMPEG, CHECK MTX VERSION)
            if (msg.Value.Function.Equals(CameraFeature))
            {
                if (msg.Value.Command == _pendingCommandName)
                    await ResolvePendingCommandAsync(msg.Value.Data);
                return;
            }

            //Commands
            if (msg.Value.Function.Equals("TOM Input"))
            {
                if (msg.Value.Command == _pendingCommandName)
                    await ResolvePendingCommandAsync(msg.Value.Data);
                return;
            }

            //TOM Status
            if (msg.Value.Function.Equals("TOM Output"))
            {
                var data = msg.Value.Data ?? "";
                var pending = Interlocked.CompareExchange(ref _pendingPushConfirm, null, null);
                var pred = _pendingPushConfirmPredicate;

                if (pending is not null
                    && _pendingPushConfirmFunction == "TOM Output"
                    && pred is not null
                    && pred(data))
                {
                    Interlocked.Exchange(ref _pendingPushConfirm, null);
                    _pendingPushConfirmFunction = null;
                    _pendingPushConfirmPredicate = null;
                    pending.TrySetResult(true);
                }
                return;
            }


        });

        _messengerService.Register<TcpSendRequestMessage>(this, (r, msg) =>
        {
            _alertService.ShowAlertAsync("Information", $"TcpSendRequestMessage: {msg}", "OK");


        });

        _messengerService.Register<TcpStatusMessage>(this, (r, msg) =>
        {
            MainThread.BeginInvokeOnMainThread(() =>
            {
                StatusText = msg.Value;
            });

            //   _alertService.ShowAlertAsync("Information", $"TcpStatusMessage: {msg.Value}", "OK");

        });

        _messengerService.Register<TcpErrorMessage>(this, (r, msg) =>
        {

            // _alertService.ShowAlertAsync("Information", $"TcpErrorMessage: {msg.Value.Message}", "OK");
            _loggerService.LogError($"TcpErrorMessage : {msg}", msg);
            MainThread.BeginInvokeOnMainThread(() =>
            {
                StatusText = msg.Value.Message;

            });

        });



        _messengerService.Register<TcpAckTimeoutMessage>(this, (r, msg) =>
        {
            //   _alertService.ShowAlertAsync("Information", $"TcpAckTimeoutMessage: {msg}", "OK");
            MainThread.BeginInvokeOnMainThread(() =>
                StatusText = $"No response to: {msg.Command}");
        });

        _messengerService.Register<TcpNackMessage>(this, (r, msg) =>
        {
            //   _alertService.ShowAlertAsync("Information", $"TcpNackMessage: {msg}", "OK");

            MainThread.BeginInvokeOnMainThread(() =>
                StatusText = $"Server rejected '{msg.Command}': {msg.Reason}");
        });


        _messengerService.Register<TcpIsConnected>(this, (r, msg) =>
        {
            //    _alertService.ShowAlertAsync("Information", $"TcpIsConnected: {msg.Value}", "OK");
            MainThread.BeginInvokeOnMainThread(() =>
            {
                IsConnected = msg.Value;
            });

        });




    }


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

    private async Task<bool> SendAndWaitForPushAsync(
        string sendFeature, string sendCommand, string sendData,
        string confirmFunction, Func<string, bool> confirmPredicate,
        TimeSpan timeout)
    {
        _pendingPushConfirmFunction = confirmFunction;
        _pendingPushConfirmPredicate = confirmPredicate;
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
            return false;
        }

        var completed = await Task.WhenAny(confirmTcs.Task, Task.Delay(timeout));

        Interlocked.Exchange(ref _pendingPushConfirm, null);
        _pendingPushConfirmFunction = null;
        _pendingPushConfirmPredicate = null;

        return completed == confirmTcs.Task && confirmTcs.Task.Result;
    }


    public async Task<bool> HandleTcpReceivedMessage(string message)
    {
        await _alertService.ShowAlertAsync("Information", $"Received {message}", "OK");
        return true;
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsNotConnected))]
    bool isConnected = false;

    public bool IsNotConnected => !IsConnected;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsNotSystemEnabled))]
    public bool isSystemEnabled = false;

    public bool IsNotSystemEnabled => !IsSystemEnabled;


    [ObservableProperty]
    private double buttonSize;

    [ObservableProperty]
    private double layoutSpacing;

    // Status text
    [ObservableProperty]
    private string statusText;

    // Theme toggle
    [ObservableProperty]
    private bool isDarkTheme;

    // Status indicators (colors as strings or Color)
    [ObservableProperty]
    private string videoStatusColor = "Red";

    [ObservableProperty]
    private string rotorStatusColor = "Red";

    // Commands
    [RelayCommand]
    private async Task Connect()
    {


        IsBusy = true;
        try
        {
            await _tcpService.StartAsync(_sqliteService.config.IPAddress, Int32.Parse(_sqliteService.config.Port));

            //StatusText = "Connected";
            //VideoStatusColor = "Green";
            //RotorStatusColor = "Blue";
        }
        catch (Exception ex)
        {
            StatusText =$"Error: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }



    }

    [RelayCommand]
    private async Task Disconnect()
    {
        IsBusy = true;
        try
        {
            await _tcpService.StopAsync();
            StatusText = "Disconnected";
            VideoStatusColor = "Red";
            RotorStatusColor = "Orange";
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task EnableSystem()
    {
        if (IsNotConnected) return;

        IsBusy = true;

        try
        {
            // CHECK FFMPEG
            StatusText = "Checking FFmpeg...";
            if (!await SendAndWaitAsync(CameraFeature, "CHECK FFMPEG", "", timeout))
            {
                StatusText = "Enable failed — FFmpeg not available";
                return;
            }

            // CHECK MTX VERSION
            StatusText = "Checking MediaMTX...";
            if (!await SendAndWaitAsync(CameraFeature, "CHECK MTX VERSION", "", timeout))
            {
                StatusText = "Enable failed — MediaMTX not available";
                return;
            }

            //Close any already open ports
            // CLOSE TOM Input
            StatusText = "Attempt to close TOM Input...";
            if (!await SendAndWaitAsync(SerialFeature, "CLOSE", "TOM Input", timeout))
            {
                StatusText = "Enable failed — could not close TOM Input";
               // return;
            }

            // CLOSE TOM Output
            StatusText = "Attempt to close Opening TOM Output...";
            if (!await SendAndWaitAsync(SerialFeature, "CLOSE", "TOM Output", timeout))
            {
                StatusText = "Enable failed — could not close TOM Output";
             //   return;
            }

            // CLOSE ROTOR
            StatusText = "Attempt to close Opening ROTOR...";
            if (!await SendAndWaitAsync(SerialFeature, "CLOSE", "ROTOR", timeout))
            {
                StatusText = "Enable failed — could not close ROTOR";
                //    return;
            }

            //Open Ports 
            // OPEN TOM Input
            StatusText = "Opening TOM Input...";
            if (!await SendAndWaitAsync(SerialFeature, "OPEN", "TOM Input", timeout))
            {
                StatusText = "Enable failed — could not open TOM Input";
                return;
            }

            // OPEN TOM Output
            StatusText = "Opening TOM Output...";
            if (!await SendAndWaitAsync(SerialFeature, "OPEN", "TOM Output", timeout))
            {
                StatusText = "Enable failed — could not open TOM Output";
                return;
            }

            // OPEN ROTOR
            StatusText = "Opening ROTOR...";
            if (!await SendAndWaitAsync(SerialFeature, "OPEN", "ROTOR", timeout))
            {
                StatusText = "Enable failed — could not open ROTOR";
            //    return;
            }

            //Send Tom On command TOMCommands.TurnOnAllSystemsCommand
            //WRITE TEXT
            // OPEN TOM Output
            StatusText = "Turning on TOM...";
            if (!await SendAndWaitForPushAsync(
                    "TOM Input", "WRITE TEXT", TOMCommands.TurnOnAllSystemsCommand,
                    "TOM Output", IsTomPowerOn,
                    timeout))
            {
                StatusText = "Enable failed — TOM did not confirm power on";
                return;
            }




            IsSystemEnabled = true;
            StatusText = "System Enabled";
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task DisableSystem()
    {
        if (IsNotConnected) return;

        IsBusy = true;


        IsBusy = true;
        try
        {


            StatusText = "Turning off TOM...";
            if (!await SendAndWaitForPushAsync(
                    "TOM Input", "WRITE TEXT", TOMCommands.TurnOffAllSystemsCommand,
                    "TOM Output", IsTomPowerOff,
                    timeout))
            {
                StatusText = "Disable failed — TOM did not confirm power off";
            }


            //Close any already open ports
            // CLOSE TOM Input
            StatusText = "Attempt to close TOM Input...";
            if (!await SendAndWaitAsync(SerialFeature, "CLOSE", "TOM Input", timeout))
            {
                StatusText = "Enable failed — could not close TOM Input";
                // return;
            }

            // CLOSE TOM Output
            StatusText = "Attempt to close Opening TOM Output...";
            if (!await SendAndWaitAsync(SerialFeature, "CLOSE", "TOM Output", timeout))
            {
                StatusText = "Enable failed — could not close TOM Output";
                //   return;
            }

            // CLOSE ROTOR
            StatusText = "Attempt to close Opening ROTOR...";
            if (!await SendAndWaitAsync(SerialFeature, "CLOSE", "ROTOR", timeout))
            {
                StatusText = "Enable failed — could not close ROTOR";
                //    return;
            }

            IsSystemEnabled = false;
            StatusText = "System Disabled";


        }


        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private void ToggleTheme(bool value)
    {
        IsDarkTheme = value;

        Application.Current.UserAppTheme =
            value ? AppTheme.Dark : AppTheme.Light;
    }

    [RelayCommand]
    private async Task Video()
    {
        if (IsNotConnected) return;

        IsBusy = true;
        try
        {
            // 1. Check how many streams are currently running in MTX
            StatusText = "Checking video streams...";
            if (!await SendAndWaitAsync(CameraFeature, "CHECK MTX STREAMS", "", timeout))
            {
                // Streams not healthy — try to create them via DISCOVER
                StatusText = "Streams not ready — attempting to start...";

                var discoverRequest = new CameraDiscoverRequest { AutoAdd = true };
                var discoverJson = JsonSerializer.Serialize(discoverRequest);

                if (!await SendAndWaitAsync(CameraFeature, "DISCOVER", discoverJson, timeout))
                {
                    StatusText = "Failed to start video streams — check camera connections";
                    return;
                }

                // Verify streams came up after discovery
                StatusText = "Verifying streams...";
                if (!await SendAndWaitAsync(CameraFeature, "CHECK MTX STREAMS", "", timeout))
                {
                    StatusText = "Video streams failed to start — check server logs";
                    return;
                }
            }

            // 2. Streams are running — open the FLIR serial port
            StatusText = "Opening FLIR control port...";
            if (!await SendAndWaitAsync(SerialFeature, "OPEN", "TOM FLIR", timeout))
            {
                StatusText = "Warning — FLIR control port unavailable, video may still work";
                return;
            }

            // 3. Navigate to the periscope page
            StatusText = "Opening video...";
            await Shell.Current.GoToAsync(nameof(PeriscopePage));
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task Rotor()
    {
        await Shell.Current.GoToAsync(nameof(RotorPage));
    }

    [RelayCommand]
    private async Task Settings()
    {
        await Shell.Current.GoToAsync(nameof(ConfigMenuPage));
    }

    public async Task ButtonLoaded()
    {
        if (_sqliteService.ConfigLoadedError)
        {
            await _alertService.ShowAlertAsync("Error", $"Failed To Load Configuration File, Failed To Load Default Settings, {_sqliteService.LastError}", "OK");
            return;
        }

        if (_sqliteService.DefaultsLoaded)
        {
            await _alertService.ShowAlertAsync("Warning", $"Failed To Load Configuration File, Restoring Default Settings", "OK");
            _sqliteService.DefaultsLoaded = false;
        }
    }

    public async Task GetConfig()
    {
        if (!await _sqliteService.GetConfigAsync())
        {
            if (await _sqliteService.SetDefaultConfig())
            {
                _sqliteService.DefaultsLoaded = true;

            }

        }


    }

    // $PBLUTP,R,PWR,STAT,1,15,0,2.61*28  → field[5] = "15" means ON
    private static bool IsTomPowerOn(string nmea)
    {
        var parts = nmea.Split(',');
        return parts.Length > 5 && parts[5] == "15";
    }

    // $PBLUTP,R,PWR,STAT,1,0,0,2.61*1C   → field[5] = "0" means OFF
    private static bool IsTomPowerOff(string nmea)
    {
        var parts = nmea.Split(',');
        return parts.Length > 5 && parts[5] == "0";
    }

    public sealed class CameraDiscoverRequest
    {
        public bool AutoAdd { get; init; } = true;
    }


}