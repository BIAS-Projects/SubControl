using SubControlMAUI.ViewModels;

namespace SubControlMAUI.Pages;

public partial class CutterCommandSettingsPage : ContentPage
{
	
	public CutterCommandSettingsPage(CutterCommandViewModel viewModel)
	{
		InitializeComponent();
			BindingContext = viewModel;
    }
}