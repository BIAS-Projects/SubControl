


using SubControlMAUI.ViewModels;

namespace SubControlMAUI
{
    public partial class AppShell : Shell
    {
        AppShellViewModel _viewModel;
        public AppShell(AppShellViewModel viewModel)
        {
            InitializeComponent();
            _viewModel = viewModel;
            BindingContext = viewModel;
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
