using SubControlMAUI.ViewModels;

namespace SubControlMAUI.Pages;

public partial class PeriscopeCommandSettingsPage : ContentPage
{
	public PeriscopeCommandSettingsPage(PeriscopeCommandViewModel viewModel)
	{
		InitializeComponent();
		BindingContext = viewModel;
    }
}