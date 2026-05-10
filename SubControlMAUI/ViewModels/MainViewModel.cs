
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using Microsoft.Extensions.Logging;
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

    private static string SerialFeature => nameof(FeatureOptionViewModel);
    private static string CameraFeature => nameof(FeatureOptionViewModel) + "CAMERA";


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
        if (_pendingCommand is null) return;

        try
        {
            var response = JsonSerializer.Deserialize<CommandResponse>(json ?? "",
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

            _pendingCommand.TrySetResult(response?.Ok == true);
        }
        catch
        {
            _pendingCommand.TrySetResult(false);
        }
    }

    private async Task<bool> SendAndWaitAsync(
        string feature, string command, string data, TimeSpan timeout)
    {
        _pendingCommandName = command;
        _pendingCommand = new TaskCompletionSource<bool>();

        var sent = await _tcpService.SendCommandAsync(
            new TCPMessageBody<string>(feature, command, data), CancellationToken.None);

        if (!sent)
        {
            _pendingCommand = null;
            _pendingCommandName = null;
            return false;
        }

        var completed = await Task.WhenAny(
            _pendingCommand.Task,
            Task.Delay(timeout));

        var success = completed == _pendingCommand.Task && _pendingCommand.Task.Result;

        _pendingCommand = null;
        _pendingCommandName = null;
        return success;
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
        var timeout = TimeSpan.FromSeconds(10);

        try
        {
            // 1. CHECK FFMPEG
            StatusText = "Checking FFmpeg...";
            if (!await SendAndWaitAsync(CameraFeature, "CHECK FFMPEG", "", timeout))
            {
                StatusText = "Enable failed — FFmpeg not available";
                return;
            }

            // 2. CHECK MTX VERSION
            StatusText = "Checking MediaMTX...";
            if (!await SendAndWaitAsync(CameraFeature, "CHECK MTX VERSION", "", timeout))
            {
                StatusText = "Enable failed — MediaMTX not available";
                return;
            }

            // 3. OPEN TOM Input
            StatusText = "Opening TOM Input...";
            if (!await SendAndWaitAsync(SerialFeature, "OPEN", "TOM Input", timeout))
            {
                StatusText = "Enable failed — could not open TOM Input";
                return;
            }

            // 4. OPEN TOM Output
            StatusText = "Opening TOM Output...";
            if (!await SendAndWaitAsync(SerialFeature, "OPEN", "TOM Output", timeout))
            {
                StatusText = "Enable failed — could not open TOM Output";
                return;
            }

            // 5. OPEN ROTOR
            StatusText = "Opening ROTOR...";
            if (!await SendAndWaitAsync(SerialFeature, "OPEN", "ROTOR", timeout))
            {
                StatusText = "Enable failed — could not open ROTOR";
                return;
            }

            //Send Tom On command TOMCommands.TurnOnAllSystemsCommand


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
        IsBusy = true;
        try
        {

            //Send turn off TOM TOMCommands.TurnOffAllSystemsCommand
            //CLose TOM input
            //Close TOM output
            //Close ROTOR


            // CLOSE commands will go here when needed
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
        //OPEN FLIR

        //Check the streams are present

        
        await Shell.Current.GoToAsync(nameof(PeriscopePage));
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






}