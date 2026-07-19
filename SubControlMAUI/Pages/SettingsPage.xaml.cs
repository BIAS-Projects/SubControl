using SubControlMAUI.ViewModels;
using System.Net.NetworkInformation;

namespace SubControlMAUI.Pages;

public partial class SettingsPage : ContentPage
{
	SettingsViewModel _viewModel;
	public SettingsPage(SettingsViewModel viewModel)
	{
		InitializeComponent();
		_viewModel = viewModel;
		BindingContext = _viewModel;
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
		_viewModel.OnAppearing();

    }


}