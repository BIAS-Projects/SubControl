namespace SubControlMAUI.Pages;

using SkiaSharp;
using SkiaSharp.Views.Maui;
using SubControlMAUI.Services;
using SubControlMAUI.ViewModels;
using System.Runtime.InteropServices;
using System.Threading.Tasks;

public partial class MainPage : ContentPage
{
    private readonly IRtspFrameDecoder _decoder;

    // Latest decoded frame — written by decode thread, read by UI thread.
    // Held as a rented ArrayPool buffer; swapped atomically in OnFrameReady.
    private volatile VideoFrame? _currentFrame;

    // Set to true in OnDisappearing to stop accepting new frames after disposal
    private volatile bool _disposed;

    // FPS counter — value types only, no allocations
    private int _frameCount;
    private DateTime _fpsTimer = DateTime.UtcNow;
    private double _fps;

    // Reused paint object — avoids allocating a new SKPaint on every frame
    private readonly SKPaint _videoPaint = new()
    {
        FilterQuality = SKFilterQuality.Low,
        IsAntialias = false
    };




    MainViewModel _viewModel;
	public MainPage(MainViewModel viewModel, IRtspFrameDecoder decoder)
	{
		InitializeComponent();
		this._viewModel = viewModel;
		BindingContext = _viewModel;
        _decoder = decoder;
    }



    private async void Button_Loaded(object sender, EventArgs e)
    {
       await _viewModel.ButtonLoaded();
    }


    // -------------------------------------------------------------------------
    // Page lifecycle — subscribe/unsubscribe here to avoid event handler leaks.
    // The decoder is a singleton that outlives the page, so if we never
    // unsubscribe, the page is kept alive by the decoder's event references
    // even after navigation.
    // -------------------------------------------------------------------------

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

        // Unsubscribe first so no new frames arrive during cleanup
        _decoder.FrameReady -= OnFrameReady;
        _decoder.StatusChanged -= OnStatusChanged;
        _decoder.ErrorOccurred -= OnErrorOccurred;

        // Release the last held frame back to the pool
        var old = Interlocked.Exchange(ref _currentFrame, null);
        old?.Release();

        // Dispose the reused paint object
        _videoPaint.Dispose();
    }

    // -------------------------------------------------------------------------
    // Decoder callbacks
    // -------------------------------------------------------------------------

    private void OnFrameReady(VideoFrame newFrame)
    {
        // Fix: if the page has been disposed, reject the frame immediately
        // rather than storing it with nobody left to release it
        if (_disposed)
        {
            newFrame.Release();
            return;
        }

        // Atomically swap in the new frame and release the old one back to the pool
        var old = Interlocked.Exchange(ref _currentFrame, newFrame);
        old?.Release();

    //    FPS counter — capture local to avoid closure allocation on every frame
       _frameCount++;
        var elapsed = (DateTime.UtcNow - _fpsTimer).TotalSeconds;
        if (elapsed >= 1.0)
        {
            _fps = _frameCount / elapsed;
            _frameCount = 0;
            _fpsTimer = DateTime.UtcNow;

            var fps = _fps;
            MainThread.BeginInvokeOnMainThread(() =>
                FpsLabel.Text = $"{fps:F1} fps");
        }

        MainThread.BeginInvokeOnMainThread(() => CanvasView.InvalidateSurface());
    }

    private void OnStatusChanged(string status)
    {
        MainThread.BeginInvokeOnMainThread(() =>
        {
            StatusLabel.Text = status;
            bool live = status.StartsWith("●");
            StatusLabel.TextColor = live
                ? Color.FromArgb("#00c853")
                : Color.FromArgb("#888888");

            Spinner.IsRunning = status.StartsWith("Init") || status.StartsWith("Connect");
            Spinner.IsVisible = Spinner.IsRunning;
            StopBtn.IsEnabled = live;
            SnapBtn.IsEnabled = live;
            ConnectBtn.IsEnabled = !live;
        });
    }

    private void OnErrorOccurred(string error)
    {
        MainThread.BeginInvokeOnMainThread(async () =>
        {
            StatusLabel.Text = "Error";
            StatusLabel.TextColor = Color.FromArgb("#ff5252");
            Spinner.IsRunning = false;
            Spinner.IsVisible = false;
            ConnectBtn.IsEnabled = true;
            StopBtn.IsEnabled = false;
            SnapBtn.IsEnabled = false;
            await DisplayAlert("Stream Error", error, "OK");
        });
    }

    // -------------------------------------------------------------------------
    // SkiaSharp paint — called on UI thread for every InvalidateSurface()
    // -------------------------------------------------------------------------
    private void OnPaintSurface(object? sender, SKPaintSurfaceEventArgs e)
    {
        var canvas = e.Surface.Canvas;
        var surface = e.Info;

        canvas.Clear(SKColors.Black);

        // Grab reference — safe to read here because OnFrameReady swaps
        // atomically and the buffer stays valid until the next swap
        var frame = _currentFrame;
        if (frame is null) return;

        var info = new SKImageInfo(
            frame.Width, frame.Height,
            SKColorType.Bgra8888, SKAlphaType.Premul);

        using var bmp = new SKBitmap();

        var gcHandle = GCHandle.Alloc(frame.Data, GCHandleType.Pinned);
        try
        {
            // Point SkiaSharp directly at the rented buffer — zero copy
            bmp.InstallPixels(info, gcHandle.AddrOfPinnedObject(), frame.Stride);

            float scaleX = (float)surface.Width / frame.Width;
            float scaleY = (float)surface.Height / frame.Height;
            float scale = Math.Min(scaleX, scaleY);
            float drawW = frame.Width * scale;
            float drawH = frame.Height * scale;
            float offsetX = (surface.Width - drawW) / 2f;
            float offsetY = (surface.Height - drawH) / 2f;

            var destRect = new SKRect(offsetX, offsetY,
                                      offsetX + drawW, offsetY + drawH);

            // _videoPaint is reused — no allocation here
            canvas.DrawBitmap(bmp, destRect, _videoPaint);
        }
        finally
        {
            gcHandle.Free();
        }
    }

    // -------------------------------------------------------------------------
    // UI event handlers
    // -------------------------------------------------------------------------

    private void OnConnectClicked(object sender, EventArgs e) =>
        StartStream(UrlEntry.Text?.Trim());

    private void OnUrlCompleted(object sender, EventArgs e) =>
        StartStream(UrlEntry.Text?.Trim());

    private void OnStopClicked(object sender, EventArgs e)
    {
        _decoder.Stop();

        // Release the last held frame back to the pool
        var old = Interlocked.Exchange(ref _currentFrame, null);
        old?.Release();

        CanvasView.InvalidateSurface();
        FpsLabel.Text = "-- fps";
    }

    private async void OnSnapClicked(object sender, EventArgs e)
    {
        var frame = _currentFrame;
        if (frame is null) return;

        try
        {
            var path = Path.Combine(
                FileSystem.AppDataDirectory,
                $"snap_{DateTime.Now:yyyyMMdd_HHmmss}.png");

            var info = new SKImageInfo(
                frame.Width, frame.Height,
                SKColorType.Bgra8888, SKAlphaType.Premul);

            var gcHandle = GCHandle.Alloc(frame.Data, GCHandleType.Pinned);
            try
            {
                using var bmp = new SKBitmap();
                bmp.InstallPixels(info, gcHandle.AddrOfPinnedObject(), frame.Stride);

                using var image = SKImage.FromBitmap(bmp);
                using var png = image.Encode(SKEncodedImageFormat.Png, 95);
                await using var fs = File.OpenWrite(path);
                png.SaveTo(fs);
            }
            finally
            {
                gcHandle.Free();
            }

            await DisplayAlert("Snapshot Saved", path, "OK");
        }
        catch (Exception ex)
        {
            await DisplayAlert("Snapshot Failed", ex.Message, "OK");
        }
    }

    // -------------------------------------------------------------------------
    // Helpers
    // -------------------------------------------------------------------------

    private void StartStream(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            DisplayAlert("Error", "Please enter a valid RTSP URL.", "OK");
            return;
        }

        // Release any frame held from a previous session
        var old = Interlocked.Exchange(ref _currentFrame, null);
        old?.Release();

        _decoder.Start(url);

        StatusLabel.Text = "Connecting…";
        StatusLabel.TextColor = Color.FromArgb("#f0a500");
        Spinner.IsRunning = true;
        Spinner.IsVisible = true;
        ConnectBtn.IsEnabled = false;
    }


}