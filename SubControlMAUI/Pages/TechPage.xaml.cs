using SubControlMAUI.ViewModels;

namespace SubControlMAUI.Pages;

public partial class TechPage : ContentPage
{
    TechViewModel _viewModel;
    public TechPage(TechViewModel viewModel)
	{
		InitializeComponent();
		_viewModel = viewModel;
		BindingContext = _viewModel;

	}
}