
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using Microsoft.Extensions.Logging;
using SubControlMAUI.Pages;
using SubControlMAUI.Services;

namespace SubControlMAUI.ViewModels;

public partial class MainViewModel : BaseViewModel
{

    SQLiteService _sqliteService;
    IAlertService _alertService;
    //ILogger<MainViewModel> _loggerService;
    //private readonly IMessenger _messengerService;
    private readonly TcpSocketService _tcpService;


    public MainViewModel(SQLiteService sqliteService,
        IAlertService alertService,
        IMessenger messengerService,
        TcpSocketService tcpService,
        ILogger<MainViewModel> loggerService) : base(messengerService, loggerService)
    {
        Title = "Main Menu";
        StatusText = "Disconnected";
        _sqliteService = sqliteService;
        _alertService = alertService;
        _tcpService = tcpService;
        LoadConfig();

    }

    private async Task LoadConfig()
    {
        if(!await _sqliteService.GetConfigAsync())
        {
            await _sqliteService.SetDefaultConfig();
            StatusText = "Disconnected - Default Configuration Loaded";
        }
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsNotConnected))]
    bool isConnected = false;

    public bool IsNotConnected => !IsConnected;

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
    private string rotorStatusColor = "Orange";

    // Commands
    [RelayCommand]
    private async Task Connect()
    {


        IsBusy = true;
        try
        {
            await _tcpService.StartAsync(_sqliteService.config.IPAddress, Int32.Parse(_sqliteService.config.Port));

            StatusText = "Connected";
            VideoStatusColor = "Green";
            RotorStatusColor = "Blue";
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