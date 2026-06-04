using SubControlMAUI.Pages;
using SubControlMAUI.ViewModels;

namespace SubControlMAUI
{
    public partial class AppShell : Shell
    {
        MainViewModel _viewModel;

        public AppShell(MainViewModel viewModel)
        {
            InitializeComponent();
            BindingContext = viewModel;
            _viewModel = viewModel;

            // All pages now need explicit registration since none are Shell tabs
            //Routing.RegisterRoute(nameof(MainPage), typeof(MainPage));
            Routing.RegisterRoute(nameof(PeriscopePage), typeof(PeriscopePage));
            Routing.RegisterRoute(nameof(RotatorPage), typeof(RotatorPage));
            Routing.RegisterRoute(nameof(SettingsPage), typeof(SettingsPage));
            Routing.RegisterRoute(nameof(ConfigMenuPage), typeof(ConfigMenuPage));
            Routing.RegisterRoute(nameof(FeatureOptionsPage), typeof(FeatureOptionsPage));
            Routing.RegisterRoute(nameof(VideoConfigPage), typeof(VideoConfigPage));
            Routing.RegisterRoute(nameof(TechPage), typeof(TechPage));
            Routing.RegisterRoute(nameof(RS232SettingsPage), typeof(RS232SettingsPage));
            Routing.RegisterRoute(nameof(I2CSettingsPage), typeof(I2CSettingsPage));
            Routing.RegisterRoute(nameof(CutterCommandSettingsPage), typeof(CutterCommandSettingsPage));
            Routing.RegisterRoute(nameof(PeriscopeCommandSettingsPage), typeof(PeriscopeCommandSettingsPage));
            Routing.RegisterRoute(nameof(RotatorOptionsPage), typeof(RotatorOptionsPage));
        }

        protected override async void OnAppearing()
        {
            base.OnAppearing();
            this.Window.MinimumHeight = 650;
            this.Window.MinimumWidth = 600;
            await _viewModel.GetConfig();
        }
    }
}