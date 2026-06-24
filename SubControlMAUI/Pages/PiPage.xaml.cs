using SubControlMAUI.ViewModels;

namespace SubControlMAUI.Pages;

public partial class PiPage : ContentPage
{
    private readonly PiViewModel _viewModel;

    public PiPage(PiViewModel viewModel)
    {
        InitializeComponent();
        BindingContext = viewModel;
        _viewModel = viewModel;

        // MAUI's ProgressBar.Progress expects 0.0–1.0.
        // The VM exposes DeployProgress as an int 0–100, so we update
        // the named bar here rather than requiring a value converter in XAML.
        _viewModel.PropertyChanged += OnViewModelPropertyChanged;
    }

    private void OnViewModelPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(PiViewModel.DeployProgress))
            DeployProgressBar.Progress = _viewModel.DeployProgress / 100.0;
    }
}