using SubControlMAUI.ViewModels;

namespace SubControlMAUI.Pages;

public partial class I2CSettingsPage : ContentPage
{
	public I2CSettingsPage(I2CSettingsViewModel viewModel)
	{	
		InitializeComponent();
		BindingContext = viewModel;
    }
}