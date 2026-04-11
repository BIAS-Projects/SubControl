using SubControlMAUI.ViewModels;

namespace SubControlMAUI.Pages;

public partial class CameraPage : ContentPage
{

	CameraViewModel _viewModel;
	public CameraPage(CameraViewModel viewModel)
	{
		InitializeComponent();
		_viewModel = viewModel;
		BindingContext = _viewModel;
	}
}