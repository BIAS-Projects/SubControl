using SubControlMAUI.ViewModels;

namespace SubControlMAUI.Pages;

public partial class FeatureOptionsPage : ContentPage
{
    public FeatureOptionsPage( FeatureOptionViewModel viewModel)
	{
		InitializeComponent();
		BindingContext = viewModel;
    }
}