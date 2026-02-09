using SubControlMAUI.ViewModels;

namespace SubControlMAUI.Pages;

public partial class RS232SettingsPage : ContentPage
{
	public RS232SettingsPage(RS232SettingsViewModel viewModel)
	{
		InitializeComponent();
		BindingContext = viewModel;
    }
}