
using SkiaSharp;
using SkiaSharp.Views.Maui;
using SubControlMAUI.ViewModels;
using System.Runtime.InteropServices;

namespace SubControlMAUI.Pages;

public partial class PeriscopePage : ContentPage
{
    private readonly PeriscopeViewModel _viewModel;


    public double ButtonSizeScalingFactor { get; set; } = 0.1;

    public double LayoutSizeScalingFactor { get; set; } = 5;


    private readonly SKPaint _videoPaint = new()
    {
        FilterQuality = SKFilterQuality.Low,
        IsAntialias = false
    };

    void OnSizeChanged(object sender, EventArgs e)
    {
        _viewModel.ButtonSize = this.Width * ButtonSizeScalingFactor;
        _viewModel.LayoutSpacing = this.Width * LayoutSizeScalingFactor;

    }


    public PeriscopePage(PeriscopeViewModel vm)
    {
        InitializeComponent();

        BindingContext = vm;

        _viewModel = vm;

        _viewModel.FrameInvalidated += OnFrameInvalidated;
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        await _viewModel.InitializeAsync();
        _viewModel.Start();
    }

    protected override void OnDisappearing()
    {
        base.OnDisappearing();

        _viewModel.Stop();
    }

    private void OnFrameInvalidated()
    {
        MainThread.BeginInvokeOnMainThread(() =>
        {
            CanvasView.InvalidateSurface();
        });
    }

    private void OnPaintSurface(object? sender, SKPaintSurfaceEventArgs e)
    {
        var canvas = e.Surface.Canvas;

        canvas.Clear(SKColors.Black);

        var frame = _viewModel.CurrentFrame;

        if (frame is null)
            return;

        var info = new SKImageInfo(
            frame.Width,
            frame.Height,
            SKColorType.Bgra8888,
            SKAlphaType.Premul);

        using var bmp = new SKBitmap();

        var gcHandle = GCHandle.Alloc(frame.Data, GCHandleType.Pinned);

        try
        {
            bmp.InstallPixels(
                info,
                gcHandle.AddrOfPinnedObject(),
                frame.Stride);

            float scaleX = (float)e.Info.Width / frame.Width;
            float scaleY = (float)e.Info.Height / frame.Height;

            float scale = Math.Min(scaleX, scaleY);

            float drawW = frame.Width * scale;
            float drawH = frame.Height * scale;

            float offsetX = (e.Info.Width - drawW) / 2f;
            float offsetY = (e.Info.Height - drawH) / 2f;

            var destRect = new SKRect(
                offsetX,
                offsetY,
                offsetX + drawW,
                offsetY + drawH);

            canvas.DrawBitmap(bmp, destRect, _videoPaint);
        }
        finally
        {
            gcHandle.Free();
        }
    }
}