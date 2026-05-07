using SubControlMAUI.ViewModels;

namespace SubControlMAUI.Pages;

public partial class VideoConfigPage : ContentPage
{
	VideoConfigViewModel _viewModel;
	public VideoConfigPage(VideoConfigViewModel viewModel)
	{
		InitializeComponent();
		_viewModel = viewModel;
		BindingContext = _viewModel;
	}
}