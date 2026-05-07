
using SubControlMAUI.ViewModels;


namespace SubControlMAUI.Pages;

public partial class ConfigMenuPage : ContentPage
{



    public double ButtonSizeScalingFactor { get; set; } = 0.1;

    public double LayoutSizeScalingFactor { get; set; } = 5;
    public double LayoutSpacing { get; set; }

    ConfigMenuViewModel _viewModel;
	public ConfigMenuPage(ConfigMenuViewModel viewModel)
	{
		InitializeComponent();
        _viewModel = viewModel;
        BindingContext = _viewModel;
   
    }

    protected override void OnSizeAllocated(double width, double height)
    {
        base.OnSizeAllocated(width, height);

        _viewModel.ButtonSize = width * ButtonSizeScalingFactor;
        _viewModel.LayoutSpacing = width * LayoutSizeScalingFactor;

    }


}