
using SkiaSharp;
using SkiaSharp.Views.Maui;
using SubControlMAUI.Services;
using SubControlMAUI.ViewModels;
using System.Runtime.InteropServices;

namespace SubControlMAUI.Pages;

public partial class PeriscopePage : ContentPage
{
    private readonly PeriscopeViewModel _viewModel;
    private readonly IRtspFrameDecoder _decoder;

    // Latest decoded frame
    private volatile VideoFrame? _currentFrame;

    public double ButtonSizeScalingFactor { get; set; } = 0.1;

    public double LayoutSizeScalingFactor { get; set; } = 5;

    // Prevent accepting frames after disposal
    private volatile bool _disposed;

    // FPS tracking
    private int _frameCount;
    private DateTime _fpsTimer = DateTime.UtcNow;
    private double _fps;

    // Reused paint object
    private readonly SKPaint _videoPaint = new()
    {
        FilterQuality = SKFilterQuality.Low,
        IsAntialias = false
    };

    public PeriscopePage(
        PeriscopeViewModel viewModel,
        IRtspFrameDecoder decoder)
    {
        InitializeComponent();

        _viewModel = viewModel;
        BindingContext = _viewModel;

        _decoder = decoder;
    }



    void OnSizeChanged(object sender, EventArgs e)
    {
        _viewModel.ButtonSize = this.Width * ButtonSizeScalingFactor;
        _viewModel.LayoutSpacing = this.Width * LayoutSizeScalingFactor;

    }

    // =========================================================
    // PAGE LIFECYCLE
    // =========================================================

    protected override void OnAppearing()
    {
        base.OnAppearing();

        _disposed = false;

        _decoder.FrameReady += OnFrameReady;
        _decoder.StatusChanged += OnStatusChanged;
        _decoder.ErrorOccurred += OnErrorOccurred;
    }

    protected override void OnDisappearing()
    {
        base.OnDisappearing();

        _disposed = true;

        _decoder.FrameReady -= OnFrameReady;
        _decoder.StatusChanged -= OnStatusChanged;
        _decoder.ErrorOccurred -= OnErrorOccurred;

        var old = Interlocked.Exchange(ref _currentFrame, null);
        old?.Release();

        _videoPaint.Dispose();
    }

    // =========================================================
    // DECODER EVENTS
    // =========================================================

    private void OnFrameReady(VideoFrame newFrame)
    {
        if (_disposed)
        {
            newFrame.Release();
            return;
        }

        var old = Interlocked.Exchange(ref _currentFrame, newFrame);
        old?.Release();

        // FPS counter
        _frameCount++;

        var elapsed = (DateTime.UtcNow - _fpsTimer).TotalSeconds;

        if (elapsed >= 1.0)
        {
            _fps = _frameCount / elapsed;

            _frameCount = 0;
            _fpsTimer = DateTime.UtcNow;

            var fps = _fps;

            MainThread.BeginInvokeOnMainThread(() =>
            {
                FpsLabel.Text = $"{fps:F1} fps";
            });
        }

        MainThread.BeginInvokeOnMainThread(() =>
        {
            CanvasView.InvalidateSurface();
        });
    }

    private void OnStatusChanged(string status)
    {
        MainThread.BeginInvokeOnMainThread(() =>
        {
            _viewModel.Status = status;

            Spinner.IsRunning =
                status.StartsWith("Init") ||
                status.StartsWith("Connect");

            Spinner.IsVisible = Spinner.IsRunning;
        });
    }

    private void OnErrorOccurred(string error)
    {
        MainThread.BeginInvokeOnMainThread(async () =>
        {
            _viewModel.Status = "Error";

            Spinner.IsRunning = false;
            Spinner.IsVisible = false;

            await DisplayAlert(
                "Stream Error",
                error,
                "OK");
        });
    }

    // =========================================================
    // SKIA RENDERING
    // =========================================================

    private void OnPaintSurface(
        object? sender,
        SKPaintSurfaceEventArgs e)
    {
        var canvas = e.Surface.Canvas;
        var surface = e.Info;

        canvas.Clear(SKColors.Black);

        var frame = _currentFrame;

        if (frame is null)
            return;

        var info = new SKImageInfo(
            frame.Width,
            frame.Height,
            SKColorType.Bgra8888,
            SKAlphaType.Premul);

        using var bitmap = new SKBitmap();

        var gcHandle = GCHandle.Alloc(
            frame.Data,
            GCHandleType.Pinned);

        try
        {
            bitmap.InstallPixels(
                info,
                gcHandle.AddrOfPinnedObject(),
                frame.Stride);

            float scaleX =
                (float)surface.Width / frame.Width;

            float scaleY =
                (float)surface.Height / frame.Height;

            float scale = Math.Min(scaleX, scaleY);

            float drawWidth = frame.Width * scale;
            float drawHeight = frame.Height * scale;

            float offsetX =
                (surface.Width - drawWidth) / 2f;

            float offsetY =
                (surface.Height - drawHeight) / 2f;

            var destRect = new SKRect(
                offsetX,
                offsetY,
                offsetX + drawWidth,
                offsetY + drawHeight);

            canvas.DrawBitmap(
                bitmap,
                destRect,
                _videoPaint);
        }
        finally
        {
            gcHandle.Free();
        }
    }

    // =========================================================
    // UI EVENTS
    // =========================================================

    private void OnCameraClicked(object sender, EventArgs e)
    {
        StartStream("rtsp://localhost:8554/usbcamera");
    }

    private void OnFlirClicked(object sender, EventArgs e)
    {
        StartStream("rtsp://localhost:8554/flir");
    }

    private void OnDualClicked(object sender, EventArgs e)
    {
        StartStream("rtsp://localhost:8554/dual");
    }

    private void OnStopClicked(object sender, EventArgs e)
    {
        _decoder.Stop();

        var old = Interlocked.Exchange(ref _currentFrame, null);
        old?.Release();

        CanvasView.InvalidateSurface();

        FpsLabel.Text = "-- fps";

        _viewModel.Status = "Stopped";
    }

    private async void OnSnapClicked(object sender, EventArgs e)
    {
        var frame = _currentFrame;

        if (frame is null)
            return;

        try
        {
            var path = Path.Combine(
                FileSystem.AppDataDirectory,
                $"snap_{DateTime.Now:yyyyMMdd_HHmmss}.png");

            var info = new SKImageInfo(
                frame.Width,
                frame.Height,
                SKColorType.Bgra8888,
                SKAlphaType.Premul);

            var gcHandle = GCHandle.Alloc(
                frame.Data,
                GCHandleType.Pinned);

            try
            {
                using var bitmap = new SKBitmap();

                bitmap.InstallPixels(
                    info,
                    gcHandle.AddrOfPinnedObject(),
                    frame.Stride);

                using var image = SKImage.FromBitmap(bitmap);

                using var png =
                    image.Encode(
                        SKEncodedImageFormat.Png,
                        95);

                await using var stream =
                    File.OpenWrite(path);

                png.SaveTo(stream);
            }
            finally
            {
                gcHandle.Free();
            }

            await DisplayAlert(
                "Snapshot Saved",
                path,
                "OK");
        }
        catch (Exception ex)
        {
            await DisplayAlert(
                "Snapshot Failed",
                ex.Message,
                "OK");
        }
    }

    // =========================================================
    // HELPERS
    // =========================================================

    private void StartStream(string url)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            DisplayAlert(
                "Error",
                "Invalid RTSP URL",
                "OK");

            return;
        }

        var old = Interlocked.Exchange(ref _currentFrame, null);
        old?.Release();

        _decoder.Start(url);

        _viewModel.Status = "Connecting...";

        Spinner.IsRunning = true;
        Spinner.IsVisible = true;
    }
}