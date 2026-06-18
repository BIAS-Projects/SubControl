using SubControlMAUI.ViewModels;

namespace SubControlMAUI.Pages;

public partial class RotatorPage : ContentPage
{
    private readonly RotatorViewModel _viewModel;

    public RotatorPage(RotatorViewModel viewModel)
    {
        InitializeComponent();

        _viewModel = viewModel;

        BindingContext = _viewModel;

        _viewModel.RefreshGaugeView = () =>
        {
            MainThread.BeginInvokeOnMainThread(() =>
            {
                GaugeView.Invalidate();
            });
        };
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