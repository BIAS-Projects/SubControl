


using SubControlMAUI.Pages;
using SubControlMAUI.ViewModels;

namespace SubControlMAUI
{
    public partial class AppShell : Shell
    {
        //AppShellViewModel _viewModel;
        MainViewModel _viewModel;
      //  public AppShell(AppShellViewModel viewModel)
        public AppShell(MainViewModel viewModel)
        {
            InitializeComponent();
            _viewModel = viewModel;
            BindingContext = viewModel;


            Routing.RegisterRoute(nameof(PeriscopePage), typeof(PeriscopePage));
            Routing.RegisterRoute(nameof(RotorPage), typeof(RotorPage));
            Routing.RegisterRoute(nameof(SettingsPage), typeof(SettingsPage));
            Routing.RegisterRoute(nameof(ConfigMenuPage), typeof(ConfigMenuPage));
            Routing.RegisterRoute(nameof(FeatureOptionsPage), typeof(FeatureOptionsPage));
            Routing.RegisterRoute(nameof(VideoConfigPage), typeof(VideoConfigPage));
            Routing.RegisterRoute(nameof(TechPage), typeof(TechPage));
            Routing.RegisterRoute(nameof(RotorPage), typeof(RotorPage));

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
