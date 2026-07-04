using SubControlMAUI.ViewModels;
using System.Net.NetworkInformation;

namespace SubControlMAUI.Pages;

public partial class LogsPage : ContentPage
{
	LogsViewModel viewModel;
	public LogsPage(LogsViewModel viewModel)
	{
		InitializeComponent();
		this.viewModel = viewModel;
		BindingContext = viewModel;
    }




}