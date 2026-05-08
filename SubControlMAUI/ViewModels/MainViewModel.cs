
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using Microsoft.Extensions.Logging;
using SubControlMAUI.Messages;
using SubControlMAUI.Pages;
using SubControlMAUI.Services;
using System.Text;

namespace SubControlMAUI.ViewModels;

public partial class MainViewModel : BaseViewModel
{

    SQLiteService _sqliteService;
    IAlertService _alertService;
    INavigationService _navigationService;
    ILogger<MainViewModel> _loggerService;
    private readonly IMessenger _messengerService;
    private readonly TcpSocketService _tcpService;


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


        _messengerService.Register<TcpDataReceivedMessage>(this, (r, msg) =>
        {

            _alertService.ShowAlertAsync("Information", $"TcpDataReceivedMessage: {msg}", "OK");

            //MainThread.BeginInvokeOnMainThread(async () =>
            //{

            //   // string message = Encoding.UTF8.GetString(msg.Value);
            //    if (!await HandleTcpReceivedMessage(msg.va))
            //    {
            //        StatusText = "Error processing Command: " + message;
            //    }
            //    else
            //    {
            //        StatusText = "Success processing Command: " + message;
            //    }


            //});

        });

        _messengerService.Register<TcpSendRequestMessage>(this, (r, msg) =>
        {
            _alertService.ShowAlertAsync("Information", $"TcpSendRequestMessage: {msg}", "OK");

            //MainThread.BeginInvokeOnMainThread(() =>
            //{
            //    StatusText = Encoding.UTF8.GetString(msg.Value);
            //});

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
            _alertService.ShowAlertAsync("Information", $"TcpAckTimeoutMessage: {msg}", "OK");
            //MainThread.BeginInvokeOnMainThread(() =>
            //    StatusText = $"No response to: {msg.Command}");
        });

        _messengerService.Register<TcpNackMessage>(this, (r, msg) =>
        {
            _alertService.ShowAlertAsync("Information", $"TcpNackMessage: {msg}", "OK");

            //MainThread.BeginInvokeOnMainThread(() =>
            //    StatusText = $"Server rejected '{msg.Command}': {msg.Reason}");
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
    private void EnableSystem()
    {
        if (IsNotConnected)
            return;
        IsSystemEnabled = true;
        StatusText = "System Enabled";
    }

    [RelayCommand]
    private void DisableSystem()
    {
        IsSystemEnabled = false;
        StatusText = "System Disabled";
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