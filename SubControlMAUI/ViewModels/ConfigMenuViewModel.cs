using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using Microsoft.Extensions.Logging;
using SubControlMAUI.Messages;
using SubControlMAUI.Pages;
using SubControlMAUI.Services;

namespace SubControlMAUI.ViewModels;

public partial class ConfigMenuViewModel : BaseViewModel
{

    ILogger<ConfigMenuViewModel> _logger;
    INavigationService _navigation;
    IMessenger _messengerService;
    public ApplicationStateService AppState { get; }

    public ConfigMenuViewModel(IMessenger messengerService,
        ILogger<ConfigMenuViewModel> logger,
        INavigationService navigation,
        ApplicationStateService applicationStateService)
    {
        _messengerService = messengerService;
        _logger = logger;
        _navigation = navigation;
        Title = "Configuration Menu";


        _messengerService.Register<TcpIsConnected>(this, (r, msg) =>
        {
            //    _alertService.ShowAlertAsync("Information", $"TcpIsConnected: {msg.Value}", "OK");
            //MainThread.BeginInvokeOnMainThread(() =>
            //{
            //    IsConnected = msg.Value;
            //});

        });
        AppState = applicationStateService;
    }



    [ObservableProperty]
    public double buttonSize;

    [ObservableProperty]
    public double layoutSpacing;

    // STATUS
    [ObservableProperty]
    private string statusText;

    // BUSY STATE (from BaseViewModel likely, but included if needed)
    // [ObservableProperty]
    // private bool isBusy;

    // THEME
    [ObservableProperty]
    private bool isDarkTheme;


    // OPTIONAL: if your XAML uses it
  //  public double ButtonSize => 70;

    // COMMANDS
    [RelayCommand]
    private async Task Ethernet()
    {
        await _navigation.GoToAsync(nameof(SettingsPage));
    }

    [RelayCommand]
    private async Task Features()
    {
        await _navigation.GoToAsync(nameof(FeatureOptionsPage));
    }

    [RelayCommand]
    private async Task Video()
    {
        await _navigation.GoToAsync(nameof(VideoConfigPage));
    }

    [RelayCommand]
    private async Task Tech()
    {
        await _navigation.GoToAsync(nameof(TechPage));
    }

    // NAVIGATION (same pattern as your MainViewModel)
    [RelayCommand]
    private async Task GoBack()
    {
        await _navigation.GoToAsync("..");
    }
}