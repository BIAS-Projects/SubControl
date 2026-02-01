using SubControlMAUI.ViewModels;
using System.Net.NetworkInformation;

namespace SubControlMAUI.Pages;

public partial class SettingsPage : ContentPage
{
	SettingsViewModel viewModel;
	public SettingsPage(SettingsViewModel viewModel)
	{
		InitializeComponent();
		this.viewModel = viewModel;
		BindingContext = viewModel;
    }




}