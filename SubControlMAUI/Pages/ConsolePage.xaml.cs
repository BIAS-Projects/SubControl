using SubControlMAUI.ViewModels;
using System.Net.NetworkInformation;

namespace SubControlMAUI.Pages;

public partial class ConsolePage : ContentPage
{
	ConsoleViewModel viewModel;
	public ConsolePage(ConsoleViewModel viewModel)
	{
		InitializeComponent();
		this.viewModel = viewModel;
		BindingContext = viewModel;
    }




}