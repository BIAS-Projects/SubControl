using SubControlMAUI.ViewModels;

namespace SubControlMAUI.Pages;

public partial class Comms : ContentPage
{
	CommsViewModel _viewModel;
	public Comms(CommsViewModel viewModel)
	{
		InitializeComponent();
		_viewModel = viewModel;
		BindingContext = _viewModel;
    }
}