using SubControlMAUI.ViewModels;

namespace SubControlMAUI.Pages;

public partial class VideoOptionsPage : ContentPage
{
    VideoOptionsViewModel _viewModel;
    public VideoOptionsPage(VideoOptionsViewModel viewModel)
	{
		InitializeComponent();
		_viewModel = viewModel;
		BindingContext = _viewModel;

	}


}