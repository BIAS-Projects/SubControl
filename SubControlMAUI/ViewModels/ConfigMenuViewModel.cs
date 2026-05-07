using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using Microsoft.Extensions.Logging;
using Microsoft.Maui.Graphics;
using SubControlMAUI.Pages;

namespace SubControlMAUI.ViewModels;

public partial class ConfigMenuViewModel : BaseViewModel
{
    public ConfigMenuViewModel(IMessenger messenger,
        ILogger<PeriscopeViewModel> logger) : base(messenger, logger)
    {
        Title = "Configuration Menu";
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
        await Shell.Current.GoToAsync(nameof(SettingsPage));
    }

    [RelayCommand]
    private async Task Features()
    {
        await Shell.Current.GoToAsync(nameof(FeatureOptionsPage));
    }

    [RelayCommand]
    private async Task Video()
    {
        await Shell.Current.GoToAsync(nameof(VideoConfigPage));
    }

    [RelayCommand]
    private async Task Tech()
    {
        await Shell.Current.GoToAsync(nameof(TechPage));
    }

    // NAVIGATION (same pattern as your MainViewModel)
    [RelayCommand]
    private async Task GoBack()
    {
        await Shell.Current.GoToAsync("..");
    }
}