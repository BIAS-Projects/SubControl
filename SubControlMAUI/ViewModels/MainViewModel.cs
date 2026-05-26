
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using Microsoft.Extensions.Logging;
using SubConsole.Models;
using SubControlMAUI.Messages;
using SubControlMAUI.Models;
using SubControlMAUI.Pages;
using SubControlMAUI.Services;
using System.Globalization;
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
    public ApplicationStateService AppState { get; }

    private TaskCompletionSource<bool>? _pendingCommand;
    private string? _pendingCommandName;


    private TaskCompletionSource<bool>? _pendingPushConfirm;
    private string? _pendingPushConfirmFunction;
    private Func<string, bool>? _pendingPushConfirmPredicate;

    //private static string SerialFeature => nameof(FeatureOptionViewModel);
    //private static string CameraFeature => nameof(FeatureOptionViewModel) + "CAMERA";

    private static string SerialFeature => nameof(MainViewModel);
    private static string CameraFeature => nameof(MainViewModel) + "CAMERA";


    private TimeSpan timeout = TimeSpan.FromSeconds(10);


    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(RotatorEnableButtonImage))]
    private bool isRotatorEnabled;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(VideoEnableButtonImage))]
    private bool isVideoEnabled;

    [ObservableProperty]
    private bool isConnected;

    public string RotatorEnableButtonImage =>
        $"{ThemePrefix}_{(IsRotatorEnabled ? "on" : "off")}_button.png";

    public string VideoEnableButtonImage =>
    $"{ThemePrefix}_{(IsVideoEnabled ? "on" : "off")}_button.png";

    private static string ThemePrefix =>
        Application.Current?.RequestedTheme == AppTheme.Dark
            ? "dark"
            : "light";

    public enum Status
    {
        Unknown,
        CommOpen,
        CommClosed,
        Enabled,
        Disabled
    }

    Dictionary<Status, Color> statusColours;

    public MainViewModel(SQLiteService sqliteService,
        IAlertService alertService,
        IMessenger messengerService,
        TcpSocketService tcpService,
        ILogger<MainViewModel> loggerService,
        INavigationService navigationService,
        ApplicationStateService applicationStateService)
    {
        Title = "Main Menu";
        StatusText = "Disconnected";
        _sqliteService = sqliteService;
        _alertService = alertService;
        _tcpService = tcpService;
        _navigationService = navigationService;
        _messengerService = messengerService;
        _loggerService = loggerService;
        AppState = applicationStateService;


        _messengerService.Register<TcpDataReceivedMessage>(this, async (r, msg) =>
        {
            //if (!msg.Value.Function.Equals(nameof(MainViewModel))) return;

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
            //MainThread.BeginInvokeOnMainThread(() =>
            //{
            //    IsConnected = msg.Value;
            //});

        });

        AppState.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(AppState.IsConnected))
            {
                OnPropertyChanged(nameof(CanEnableSystem));
                OnPropertyChanged(nameof(CanDisableSystem));
                IsConnected = AppState.IsConnected;
            }
        };

        statusColours = new Dictionary<Status, Color>
        {
            { Status.CommOpen, Colors.Orange },
            { Status.CommClosed, Colors.Red },
            { Status.Enabled, Colors.Green },
            { Status.Disabled, Colors.Orange },
            { Status.Unknown, Colors.Gray }
        };


        videoStatusColor = statusColours[Status.Unknown];
        rotatorStatusColor = statusColours[Status.Unknown];

        App.ThemeChanged += () =>
        {
            OnPropertyChanged(nameof(RotatorEnableButtonImage));
            OnPropertyChanged(nameof(VideoEnableButtonImage));
        };

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
    [NotifyPropertyChangedFor(nameof(IsNotSystemEnabled))]
    [NotifyPropertyChangedFor(nameof(CanEnableSystem))]
    [NotifyPropertyChangedFor(nameof(CanDisableSystem))]
    public bool isSystemEnabled = false;

    public bool IsNotSystemEnabled => !IsSystemEnabled;

    public bool CanEnableSystem => AppState.IsConnected && !IsSystemEnabled;

    public bool CanDisableSystem => AppState.IsConnected && IsSystemEnabled;




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
    private Color videoStatusColor;

    [ObservableProperty]
    private Color rotatorStatusColor;

    [RelayCommand]
    private async Task Connect()
    {
        IsBusy = true;
        try
        {
            await _tcpService.StartAsync(
                _sqliteService.config.IPAddress,
                int.Parse(_sqliteService.config.Port));

            if (!AppState.IsConnected)
            {
                StatusText = "Connection failed";
                return;
            }

            StatusText = "Querying device capabilities...";

            var serialFunctions = await QuerySerialRegisteredAsync();

            foreach (var feature in AppState.Features.ToList())
            {
                bool fitted = serialFunctions.Contains(feature.Name);
                feature.IsFitted = fitted;
                feature.IsCommPortOpen = false;
                feature.IsEnabled = false;
                AppState.UpdateFeature(feature);
            }

            // Try to open TOM Input comm port if fitted
            var tomInput = AppState.GetFeatureByName(Feature.TOMInput);
            if (tomInput?.IsFitted == true)
            {
                StatusText = "Opening TOM Input...";
                if (await OpenFeatureCommPort(Feature.TOMInput))
                {
                    tomInput.IsCommPortOpen = true;
                    AppState.UpdateFeature(tomInput);
                }
            }

            // Try to open Rotator comm port if fitted
            var rotator = AppState.GetFeatureByName(Feature.RotatorName);
            if (rotator?.IsFitted == true)
            {
                StatusText = "Opening Rotator...";
                if (await OpenFeatureCommPort(Feature.RotatorName))
                {
                    rotator.IsCommPortOpen = true;
                    AppState.UpdateFeature(rotator);
                }
            }

            // Update status colours based on final feature state
            MainThread.BeginInvokeOnMainThread(() =>
            {
                VideoStatusColor = tomInput switch
                {
                    null => statusColours[Status.Unknown],
                    { IsFitted: false } => statusColours[Status.Unknown],
                    { IsCommPortOpen: true } => statusColours[Status.CommOpen],
                    _ => statusColours[Status.CommClosed]
                };

                RotatorStatusColor = rotator switch
                {
                    null => statusColours[Status.Unknown],
                    { IsFitted: false } => statusColours[Status.Unknown],
                    { IsCommPortOpen: true } => statusColours[Status.CommOpen],
                    _ => statusColours[Status.CommClosed]
                };
            });

            StatusText = "Connected";
        }
        catch (Exception ex)
        {
            StatusText = $"Error: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task<HashSet<string>> QuerySerialRegisteredAsync()
    {
        var tcs = new TaskCompletionSource<string?>();
        const string command = "LIST REGISTERED";

        void Handler(object r, TcpDataReceivedMessage msg)
        {
            if (msg.Value.Function.Equals(SerialFeature) &&
                msg.Value.Command.Equals(command))
            {
                tcs.TrySetResult(msg.Value.Data);
            }
        }

        _messengerService.Register<TcpDataReceivedMessage>(tcs, Handler);

        try
        {
            var sent = await _tcpService.SendCommandAsync(
                new TCPMessageBody<string>(SerialFeature, command, ""),
                CancellationToken.None);

            if (!sent) return new HashSet<string>();

            var completed = await Task.WhenAny(tcs.Task, Task.Delay(timeout));
            if (completed != tcs.Task) return new HashSet<string>();

            var json = tcs.Task.Result;
            if (string.IsNullOrWhiteSpace(json)) return new HashSet<string>();

            var response = JsonSerializer.Deserialize<ListRegisteredResponse>(
                json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

            return response?.Ok == true && response.Data is not null
                ? response.Data
                    .Select(e => e.FunctionName)
                    .Where(n => !string.IsNullOrWhiteSpace(n))
                    .ToHashSet()
                : new HashSet<string>();
        }
        catch
        {
            return new HashSet<string>();
        }
        finally
        {
            _messengerService.Unregister<TcpDataReceivedMessage>(tcs);
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
            VideoStatusColor = statusColours[Status.Unknown];
            RotatorStatusColor = statusColours[Status.Unknown];
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task EnableSystem()
    {
        if (AppState.IsNotConnected) return;

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

            // CLOSE ROTATOR
            StatusText = "Attempt to close Opening ROTATOR...";
            if (!await SendAndWaitAsync(SerialFeature, "CLOSE", "ROTATOR", timeout))
            {
                StatusText = "Enable failed — could not close ROTATOR";
                //    return;
            }


            // CLOSE FLIR
            StatusText = "Attempt to close FLIR...";
            if (!await SendAndWaitAsync(SerialFeature, "CLOSE", "TOM FLIR", timeout))
            {
                StatusText = "Enable failed — could not close FLIR";
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

            // OPEN ROTATOR
            StatusText = "Opening ROTATOR...";
            if (!await SendAndWaitAsync(SerialFeature, "OPEN", "ROTATOR", timeout))
            {
                StatusText = "Enable failed — could not open ROTATOR";
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
        if (AppState.IsNotConnected) return;

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
            StatusText = "Attempt to close TOM Output...";
            if (!await SendAndWaitAsync(SerialFeature, "CLOSE", "TOM Output", timeout))
            {
                StatusText = "Enable failed — could not close TOM Output";
                //   return;
            }

            // CLOSE ROTATOR
            StatusText = "Attempt to close ROTATOR...";
            if (!await SendAndWaitAsync(SerialFeature, "CLOSE", "ROTATOR", timeout))
            {
                StatusText = "Enable failed — could not close ROTATOR";
                //    return;
            }

            // CLOSE FLIR
            StatusText = "Attempt to close FLIR...";
            if (!await SendAndWaitAsync(SerialFeature, "CLOSE", "TOM FLIR", timeout))
            {
                StatusText = "Enable failed — could not close FLIR";
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

    private async Task GlobalToggleEnable()
    {
        foreach(Feature feature in AppState.Features)
        {
            if(feature.Name.Equals(Feature.RotatorName))
            {
                if(await EnableFeature(feature.Name))
                {
                    RotatorStatusColor = statusColours[Status.Enabled];
                }
               else
                {
                    RotatorStatusColor = statusColours[Status.Disabled];
                }
            }
            if (feature.Name.Equals(Feature.TOMInput))
            {
                if (await EnableFeature(feature.Name))
                {
                    RotatorStatusColor = statusColours[Status.Enabled];
                }
                else
                {
                    RotatorStatusColor = statusColours[Status.Disabled];
                }
            }
        }
    }

    private async Task<Color> ToggleCommPortOpen(Feature feature)
    {
        if (!feature.IsFitted)
            return statusColours[Status.Unknown];

        if (!feature.IsCommPortOpen)
        {
            if (!await OpenFeatureCommPort(feature.Name))
            {
                return statusColours[Status.CommClosed];
            }
            feature.IsCommPortOpen = true;
            if (!AppState.UpdateFeature(feature))
            {
                StatusText = $"Error updating application state for: {feature.Name}";
            }
            return statusColours[Status.CommOpen];
        }

        if (!await CloseFeatureCommPort(feature.Name))
        {
            return statusColours[Status.CommClosed];
        }
        feature.IsCommPortOpen = false;
        if (!AppState.UpdateFeature(feature))
        {
            StatusText = $"Error updating application state for: {feature.Name}";
        }
        return statusColours[Status.CommClosed];

    }

    private async Task<Color> ToggleEnable(Feature feature)
    {
        if (!feature.IsFitted)
            return statusColours[Status.Unknown];

        if (!feature.IsEnabled)
        {
            if (!await EnableFeature(feature.Name))
            {
                return statusColours[Status.Disabled];
            }
            feature.IsEnabled = true;
            if (!AppState.UpdateFeature(feature))
            {
                StatusText = $"Error updating application state for: {feature.Name}";
            }
            return statusColours[Status.Enabled];
        }

        if (!await DisableFeature(feature.Name))
        {
            return statusColours[Status.Disabled];
        }
        feature.IsEnabled = false;
        feature.IsCommPortOpen = false;
        if (!AppState.UpdateFeature(feature))
        {
            StatusText = $"Error updating application state for: {feature.Name}";
        }
        return statusColours[Status.CommClosed];

    }



    //Probably want to separate this into two functions
    private async Task<Color> ChangeFeatureState(Feature feature)
    {
        if (!feature.IsFitted)
            return statusColours[Status.Unknown];
        if (!feature.IsCommPortOpen)
        { 
            if (!await OpenFeatureCommPort(feature.Name))
            {
                feature.IsCommPortOpen = false;
                if (!AppState.UpdateFeature(feature))
                {
                    StatusText = $"Error updating application state for: {feature.Name}";
                }
                return statusColours[Status.CommClosed];
            }
            feature.IsCommPortOpen = true;
            if (!AppState.UpdateFeature(feature))
            {
                StatusText = $"Error updating application state for: {feature.Name}";
            }
            return statusColours[Status.CommOpen];
        }
        if(!feature.IsEnabled)
        {
            if (!await EnableFeature(feature.Name))
            {
                feature.IsEnabled = false;
                if (!AppState.UpdateFeature(feature))
                {
                    StatusText = $"Error updating application state for: {feature.Name}";
                }
                return statusColours[Status.Disabled];
            }
            feature.IsEnabled = true;
            if (!AppState.UpdateFeature(feature))
            {
                StatusText = $"Error updating application state for: {feature.Name}";
            }
            return statusColours[Status.Enabled];
        }
        StatusText = $"Feature state not found for: {feature.Name}";
        return statusColours[Status.Unknown];
    }





    private async Task<bool> OpenFeatureCommPort(string featureName)
    {
        IsBusy = true;
        if (AppState.IsNotConnected) return false;
        try
        {

            StatusText = $"Opening {featureName}...";
            if (!await SendAndWaitAsync(SerialFeature, "OPEN", featureName, timeout))
            {
                StatusText = $"{featureName} comm port open failed";
                return false;
            }
            StatusText = $"{featureName} comm port opened";
            return true;
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task<bool> CloseFeatureCommPort(string featureName)
    {
        IsBusy = true;
        if (AppState.IsNotConnected) return false;
        try
        {
            StatusText = $"Closing {featureName}...";
            if (!await SendAndWaitAsync(SerialFeature, "CLOSE", featureName, timeout))
            {
                StatusText = $"{featureName} comm port close failed";
                return false;
            }
            StatusText = $"{featureName} comm port closed";
            return true;
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task<bool> EnableFeature (string featureName)
    {
        IsBusy = true;

        if (AppState.IsNotConnected) return false;

        Feature feature = AppState.GetFeatureByName(featureName);

        StatusText = $"Enabling {featureName}...";

        if (feature is null)
        {
            StatusText = $"Feature: {featureName} is not supported";
            return false;
        }

        if(!feature.IsCommPortOpen)
        {
            StatusText = $"Feature: {featureName} communications port is closed";
            return false;
        }


        try
        {
            if(featureName.Equals(Feature.RotatorName))
            {
                if(await EnableRotator())
                {
                    StatusText = $"Feature: {featureName} Enabled";
                    return true;
                }
            }
            if (featureName.Equals(Feature.VideoName))
            {
                if (await EnableVideo())
                {
                    StatusText = $"Feature: {featureName} Enabled";
                    return true;
                }
            }

            return false;
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task<bool> EnableRotator()
    {
        if (!await SendAndWaitForPushAsync(
                Feature.RotatorName,
                "WRITE TEXT",
                Models.Rotator.GetFirmwareVersion,
                Feature.RotatorName,
                response => response.Contains("AMRV"),
                timeout))
        {
            StatusText = "Rotator enable failed";
            return false;
        }
        return true;
    }

    private async Task<bool> EnableVideo ()
    {
        // CHECK FFMPEG
        StatusText = "Checking FFmpeg...";
        if (!await SendAndWaitAsync(CameraFeature, "CHECK FFMPEG", "", timeout))
        {
            StatusText = "Enable failed — FFmpeg not available";
            return false;
        }

        // CHECK MTX VERSION
        StatusText = "Checking MediaMTX...";
        if (!await SendAndWaitAsync(CameraFeature, "CHECK MTX VERSION", "", timeout))
        {
            StatusText = "Enable failed — MediaMTX not available";
            return false ;
        }

        if (!await SendAndWaitForPushAsync(
                "TOM Input", "WRITE TEXT", TOMCommands.TurnOnAllSystemsCommand,
                "TOM Output", IsTomPowerOn,
                timeout))
        {
            StatusText = "Enable failed — TOM did not confirm power on";
            return false;
        }
        return true;

    }

    private async Task<bool> DisableFeature(string featureName)
    {
        IsBusy = true;

        if (AppState.IsNotConnected) return false;

        Feature feature = AppState.GetFeatureByName(featureName);

        StatusText = $"Disabling {featureName}...";

        if (feature is null)
        {
            StatusText = $"Feature: {featureName} is not supported";
            return false;
        }

        if (feature.IsCommPortOpen)
        {
            StatusText = $"Feature: {featureName} communications port is closed";
            return false;
        }


        try
        {
            if (featureName.Equals(Feature.RotatorName))
            {
                StatusText = $"Feature: {featureName} Disabled";
                return true;
                
            }
            if (featureName.Equals(Feature.VideoName))
            {
                if (await DisableVideo())
                {
                    StatusText = $"Feature: {featureName} Disabled";
                    return true;
                }
            }
            return false;
        }
        finally
        {
            IsBusy = false;
        }
    }


    private async Task<bool> DisableVideo()
    {

        if (!await SendAndWaitForPushAsync(
                Feature.TOMInput, "WRITE TEXT", TOMCommands.TurnOffAllSystemsCommand,
                Feature.TOMOutput, IsTomPowerOff,
                timeout))
        {
            StatusText = "Disable failed — TOM did not confirm power off";
        }
        return true;
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
        if (AppState.IsNotConnected) return;

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
                StatusText = "Warning — FLIR control port unavailable";
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
    private async Task Rotator()
    {
        await Shell.Current.GoToAsync(nameof(RotatorPage));
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


    [RelayCommand]
    private void ToggleVideoEnable()
    {
        IsVideoEnabled = !IsVideoEnabled;
    }

    [RelayCommand]
    private void ToggleRotatorEnable()
    {
        IsRotatorEnabled = !IsRotatorEnabled;
    }





    private sealed class SerialRegisteredEntry
    {
        public string FunctionName { get; init; } = "";
        public string CurrentPort { get; init; } = "";
    }

    private sealed class CameraRegisteredEntry
    {
        public string StreamPathName { get; init; } = "";
    }

    private sealed class ListCameraRegisteredResponse
    {
        public bool Ok { get; init; }
        public List<CameraRegisteredEntry>? Data { get; init; }
    }

}