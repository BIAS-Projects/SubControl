
using Microsoft.Extensions.DependencyInjection;
using SubControlMAUI.ViewModels;

namespace SubControlMAUI
{
    public partial class App : Application
    {
        AppShellViewModel _viewModel;
        public App(AppShellViewModel viewModel)
        {
            InitializeComponent();
            _viewModel = viewModel;
        }

        protected override Window CreateWindow(IActivationState? activationState)
        {
            return new Window(new AppShell(_viewModel));
        }
    }
}