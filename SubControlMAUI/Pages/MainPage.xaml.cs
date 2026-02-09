namespace SubControlMAUI.Pages;
using SubControlMAUI.ViewModels;
using System.Threading.Tasks;

public partial class MainPage : ContentPage
{
	MainViewModel _viewModel;
	public MainPage(MainViewModel viewModel)
	{
		InitializeComponent();
		this._viewModel = viewModel;
		BindingContext = _viewModel;
    }

    //protected override async void OnAppearing()
    //{
    //    base.OnAppearing();
    //    await _viewModel.GetConfig();


    //}

    private async void Button_Loaded(object sender, EventArgs e)
    {
       await _viewModel.ButtonLoaded();
    }
}