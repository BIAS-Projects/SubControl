using SubControlMAUI.ViewModels;

namespace SubControlMAUI.Pages;

public partial class RotatorOptionsPage : ContentPage
{
    RotatorOptionsViewModel _viewModel;
    public RotatorOptionsPage(RotatorOptionsViewModel viewModel)
	{
		InitializeComponent();
		_viewModel = viewModel;
		BindingContext = _viewModel;

	}
}