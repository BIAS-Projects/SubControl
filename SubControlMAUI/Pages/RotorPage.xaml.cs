using SubControlMAUI.ViewModels;

namespace SubControlMAUI.Pages;

public partial class RotorPage : ContentPage
{
    private readonly RotorViewModel _viewModel;

    public RotorPage(RotorViewModel viewModel)
    {
        InitializeComponent();

        _viewModel = viewModel;

        BindingContext = _viewModel;
    }

    protected override void OnAppearing()
    {
        base.OnAppearing();

        _viewModel.OnAppearing();
    }

    protected override void OnDisappearing()
    {
        base.OnDisappearing();

        _viewModel.OnDisappearing();
    }
}