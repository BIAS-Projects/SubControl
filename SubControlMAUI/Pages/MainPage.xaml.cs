namespace SubControlMAUI.Pages;

using SkiaSharp;
using SkiaSharp.Views.Maui;
using SubControlMAUI.Services;
using SubControlMAUI.ViewModels;
using System.Runtime.InteropServices;
using System.Threading.Tasks;

public partial class MainPage : ContentPage
{


    MainViewModel _viewModel;

    public double ButtonSizeScalingFactor { get; set; } = 0.1;

    public double LayoutSizeScalingFactor { get; set; } = 5;

    public MainPage(MainViewModel viewModel)
	{
		InitializeComponent();
		this._viewModel = viewModel;
		BindingContext = _viewModel;

        this.SizeChanged += OnSizeChanged;
    }

    void OnSizeChanged(object sender, EventArgs e)
    {
        _viewModel.ButtonSize = this.Width * ButtonSizeScalingFactor;
        _viewModel.LayoutSpacing = this.Width * LayoutSizeScalingFactor;

    }

    private void OnThemeSwitchToggled(object sender, ToggledEventArgs e)
    {
        if (BindingContext is MainViewModel vm)
            vm.ToggleThemeCommand.Execute(e.Value);
    }


}