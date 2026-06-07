namespace SubControlMAUI.ViewModels;

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using Microsoft.Extensions.Logging;
using SkiaSharp;
using SubConsole.Models;
using SubControlMAUI.Messages;
using SubControlMAUI.Models;
using SubControlMAUI.Services;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Xml.Serialization;

public partial class PeriscopeViewModel : BaseViewModel, IDisposable
{
    private readonly SQLiteService _sqliteService;
    private readonly IAlertService _alertService;
    private readonly ILogger<PeriscopeViewModel> _logger;
    private readonly IMessenger _messengerService;
    private readonly IRtspFrameDecoder _decoder;
    private readonly IRtspFrameDecoder _decoder2;

    /// <summary>
    /// Per-ViewModel dispatcher — owns the one-slot pending state for this
    /// ViewModel.  Injected so it can be mocked in tests.
    /// </summary>
    private readonly CommandDispatcherService _dispatcher;

    public ApplicationStateService AppState { get; }

    // -------------------------------------------------------------------------
    // Frame storage
    // -------------------------------------------------------------------------

    private volatile VideoFrame? _currentFrame;
    private volatile bool _disposed;

    public VideoFrame? CurrentFrame => _currentFrame;

    /// <summary>Notify view that a repaint is required.</summary>
    public event Action? FrameInvalidated;

    private volatile VideoFrame? _currentFrame2;
    private volatile bool _isDualStream;

    public VideoFrame? CurrentFrame2 => _currentFrame2;
    public bool IsDualStream => _isDualStream;

    // -------------------------------------------------------------------------
    // FPS
    // -------------------------------------------------------------------------

    private int _frameCount;
    private DateTime _fpsTimer = DateTime.UtcNow;

    [ObservableProperty]
    private double fps;

    // -------------------------------------------------------------------------
    // UI state
    // -------------------------------------------------------------------------

    private readonly TimeSpan _timeout = TimeSpan.FromSeconds(10);

    [ObservableProperty]
    private string statusText = string.Empty;

    [ObservableProperty]
    private string rtspVideoUrl = string.Empty;

    [ObservableProperty]
    private string rtspFLIRUrl = string.Empty;

    public string MTXRTSPPort { get; set; } = "8554";
    public string VideoEndPoint { get; set; } = "/usbcamera";
    public string FlirEndpoint { get; set; } = "/flir";

    [ObservableProperty]
    private bool isStreaming;

    [ObservableProperty]
    private double buttonSize;

    [ObservableProperty]
    private double layoutSpacing;

    // -------------------------------------------------------------------------
    // Constructor
    // -------------------------------------------------------------------------

    public PeriscopeViewModel(
        SQLiteService sqliteService,
        IAlertService alertService,
        IMessenger messengerService,
        CommandDispatcherService dispatcher,
        ILogger<PeriscopeViewModel> logger,
        IRtspFrameDecoder decoder,
        IRtspFrameDecoder decoder2,
        ApplicationStateService applicationStateService)
    {
        Title = "Periscope";

        _sqliteService = sqliteService;
        _alertService = alertService;
        _messengerService = messengerService;
        _dispatcher = dispatcher;
        _dispatcher.Owner = nameof(PeriscopeViewModel);
        _logger = logger;
        _decoder = decoder;
        _decoder2 = decoder2;
        AppState = applicationStateService;

        ButtonSize = 20.0;

        _messengerService.Register<TcpIsConnected>(this, (_, msg) =>
        {
            if (!msg.Value)
                MainThread.BeginInvokeOnMainThread(async () =>
                    await Shell.Current.GoToAsync("//MainPage"));
        });

        RegisterMessages();
    }

    // -------------------------------------------------------------------------
    // Message registration
    // -------------------------------------------------------------------------

    private void RegisterMessages()
    {
        _messengerService.Register<TcpDataReceivedMessage>(this, (_, msg) =>
        {
            // Route ALL incoming messages through the dispatcher first.
            // The dispatcher will satisfy any pending SendAndWaitAsync /
            // SendAndWaitForPushAsync that matches function + command.
            _dispatcher.HandleIncoming(
                msg.Value.Function,
                msg.Value.Command,
                msg.Value.Data);
        });

        _messengerService.Register<TcpStatusMessage>(this, (_, msg) =>
            MainThread.BeginInvokeOnMainThread(() => StatusText = msg.Value));

        _messengerService.Register<TcpErrorMessage>(this, (_, msg) =>
        {
            _logger?.LogError("TcpErrorMessage: {Message}", msg.Value.Message);
            MainThread.BeginInvokeOnMainThread(() => StatusText = msg.Value.Message);
        });

        _messengerService.Register<TcpAckTimeoutMessage>(this, (_, msg) =>
            MainThread.BeginInvokeOnMainThread(
                () => StatusText = $"No response to: {msg.Command}"));

        _messengerService.Register<TcpNackMessage>(this, (_, msg) =>
            MainThread.BeginInvokeOnMainThread(
                () => StatusText = $"Server rejected '{msg.Command}': {msg.Reason}"));


    }

    // -------------------------------------------------------------------------
    // Lifecycle
    // -------------------------------------------------------------------------

    public async Task InitializeAsync()
    {
        try
        {
            IsBusy = true;

            if (!await _sqliteService.GetConfigAsync())
            {
                StatusText = "Failed to load config";
                return;
            }

            // Currently using localhost; swap to _sqliteService.config.IPAddress
            // + MTXRTSPPort + endpoint when the server is remote.
            RtspVideoUrl = "rtsp://localhost:8554/usbcamera";
            RtspFLIRUrl = "rtsp://localhost:8554/flir";
        }
        catch (Exception ex)
        {
            StatusText = ex.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    public void Start()
    {
        _disposed = false;

        _decoder.FrameReady += OnFrameReady;
        _decoder.StatusChanged += Decoder_StatusChanged;
        _decoder.ErrorOccurred += OnErrorOccurred;

        _decoder2.FrameReady += OnFrameReady2;
        _decoder2.StatusChanged += Decoder_StatusChanged;
        _decoder2.ErrorOccurred += OnErrorOccurred;
    }

    public void Stop()
    {
        _disposed = true;

        _decoder.FrameReady -= OnFrameReady;
        _decoder.StatusChanged -= Decoder_StatusChanged;
        _decoder.ErrorOccurred -= OnErrorOccurred;

        _decoder2.FrameReady -= OnFrameReady2;
        _decoder2.StatusChanged -= Decoder_StatusChanged;
        _decoder2.ErrorOccurred -= OnErrorOccurred;

        Interlocked.Exchange(ref _currentFrame, null)?.Release();
        Interlocked.Exchange(ref _currentFrame2, null)?.Release();
    }

    public void Dispose() => Stop();

    // -------------------------------------------------------------------------
    // Decoder callbacks
    // -------------------------------------------------------------------------

    private void OnFrameReady(VideoFrame newFrame)
    {
        if (_disposed) { newFrame.Release(); return; }

        Interlocked.Exchange(ref _currentFrame, newFrame)?.Release();

        _frameCount++;
        var elapsed = (DateTime.UtcNow - _fpsTimer).TotalSeconds;
        if (elapsed >= 1.0)
        {
            var fps = _frameCount / elapsed;
            _frameCount = 0;
            _fpsTimer = DateTime.UtcNow;
            MainThread.BeginInvokeOnMainThread(() => Fps = fps);
        }

        MainThread.BeginInvokeOnMainThread(() => FrameInvalidated?.Invoke());
    }

    private void OnFrameReady2(VideoFrame newFrame)
    {
        if (_disposed) { newFrame.Release(); return; }

        Interlocked.Exchange(ref _currentFrame2, newFrame)?.Release();
        MainThread.BeginInvokeOnMainThread(() => FrameInvalidated?.Invoke());
    }

    private void Decoder_StatusChanged(string status)
    {
        MainThread.BeginInvokeOnMainThread(() =>
        {
            StatusText = status;
            IsStreaming = status.StartsWith("●");
            IsBusy = status.StartsWith("Init") || status.StartsWith("Connect");
        });
    }

    private void OnErrorOccurred(string error)
    {
        MainThread.BeginInvokeOnMainThread(() =>
        {
            StatusText = $"Stream Error: {error}";
            IsBusy = false;
            IsStreaming = false;
        });
    }

    // -------------------------------------------------------------------------
    // Commands — video control
    // -------------------------------------------------------------------------

    [RelayCommand]
    private void DisplayDualVideo()
    {
        _isDualStream = true;
        Interlocked.Exchange(ref _currentFrame, null)?.Release();
        Interlocked.Exchange(ref _currentFrame2, null)?.Release();

        StatusText = "Connecting dual stream...";
        IsBusy = true;

        _decoder.Start(RtspVideoUrl);
        _decoder2.Start(RtspFLIRUrl);
    }

    [RelayCommand]
    private void DisplayStandardVideo()
    {
        _isDualStream = false;
        _decoder2.Stop();
        StartStream(RtspVideoUrl);
    }

    [RelayCommand]
    private void DisplayFLIRVideo()
    {
        _isDualStream = false;
        _decoder2.Stop();
        StartStream(RtspFLIRUrl);
    }

    [RelayCommand]
    private void StopVideo()
    {
        _isDualStream = false;
        _decoder.Stop();
        _decoder2.Stop();

        Interlocked.Exchange(ref _currentFrame, null)?.Release();
        Interlocked.Exchange(ref _currentFrame2, null)?.Release();

        MainThread.BeginInvokeOnMainThread(() => Fps = 0);
        FrameInvalidated?.Invoke();
    }

    // -------------------------------------------------------------------------
    // Commands — FLIR palette (all use Request/Response via the dispatcher)
    //
    // "TOM FLIR" uses Request/Response: we send "WRITE TEXT" and the server
    // echoes it back on the "TOM FLIR" channel with a CommandResponse body.
    // -------------------------------------------------------------------------

    [RelayCommand]
    private Task SetFlirWhitehot()
        => SetFlirPaletteAsync("White hot", FLIR.LUTtoWHITEHOT);

    [RelayCommand]
    private Task SetFlirRainbow()
        => SetFlirPaletteAsync("Rainbow", FLIR.LUTtoRAINBOW);

    [RelayCommand]
    private Task SetFlirBlackhot()
        => SetFlirPaletteAsync("Black hot", FLIR.LUTtoBLACKHOT);

    [RelayCommand]
    private Task SetFlirIronbow()
        => SetFlirPaletteAsync("Iron bow", FLIR.LUTtoIRONBOW);

    [RelayCommand]
    private Task SetFlirGlowbow()
        => SetFlirPaletteAsync("Glow bow", FLIR.LUTtoGLOBOW);

    private async Task SetFlirPaletteAsync(string paletteName, string lutCommand)
    {
        IsBusy = true;
        try
        {
            StatusText = $"Attempting to set FLIR to {paletteName}...";

            bool ok = await _dispatcher.SendAndWaitAsync(
                "TOM FLIR", "WRITE TEXT", lutCommand, _timeout);

            StatusText = ok
                ? $"FLIR set to {paletteName}"
                : "Update failed — could not set colour palette";
        }
        finally
        {
            IsBusy = false;
        }
    }

    // -------------------------------------------------------------------------
    // Commands — snapshot
    // -------------------------------------------------------------------------

    [RelayCommand]
    private async Task TakeSnapShot()
    {
        StatusText = "Taking Snapshot...";
        IsBusy = true;

        try
        {
            if (_isDualStream)
                await SaveDualSnapshotAsync();
            else
                await SaveSingleSnapshotAsync();
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task SaveDualSnapshotAsync()
    {
        var frame1 = _currentFrame;
        var frame2 = _currentFrame2;
        if (frame1 is null && frame2 is null) return;

        try
        {
            string path = Path.Combine(
                AppState.SnapShotPath,
                $"snap_dual_{DateTime.Now:yyyyMMdd_HHmmss}.png");

            int w1 = frame1?.Width ?? 0, h1 = frame1?.Height ?? 0;
            int w2 = frame2?.Width ?? 0, h2 = frame2?.Height ?? 0;
            int totalWidth = w1 + w2;
            int totalHeight = Math.Max(h1, h2);

            if (totalWidth <= 0 || totalHeight <= 0) return;

            var compositeInfo = new SKImageInfo(
                totalWidth, totalHeight, SKColorType.Bgra8888, SKAlphaType.Premul);

            using var surface = SKSurface.Create(compositeInfo);
            var canvas = surface.Canvas;
            canvas.Clear(SKColors.Black);

            if (frame1 is not null)
                DrawFrameToCanvas(canvas, frame1, new SKRect(0, 0, w1, totalHeight));

            if (frame2 is not null)
                DrawFrameToCanvas(canvas, frame2, new SKRect(w1, 0, w1 + w2, totalHeight));

            using var image = surface.Snapshot();
            using var png = image.Encode(SKEncodedImageFormat.Png, 95);
            await using var fs = File.OpenWrite(path);
            png.SaveTo(fs);

            StatusText = "Dual Snapshot Saved";
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Dual snapshot failed");
            StatusText = "Snapshot Failed";
        }
    }

    private async Task SaveSingleSnapshotAsync()
    {
        var frame = _currentFrame;
        if (frame is null) return;

        try
        {
            string path = Path.Combine(
                AppState.SnapShotPath,
                $"snap_{DateTime.Now:yyyyMMdd_HHmmss}.png");

            var info = new SKImageInfo(
                frame.Width, frame.Height, SKColorType.Bgra8888, SKAlphaType.Premul);

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

            StatusText = "Snapshot Saved";
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Snapshot failed");
            StatusText = "Snapshot Failed";
        }
    }

    // Mirrors the letterboxing logic in PeriscopePage.xaml.cs
    private static void DrawFrameToCanvas(SKCanvas canvas, VideoFrame frame, SKRect destRect)
    {
        float scaleX = destRect.Width / frame.Width;
        float scaleY = destRect.Height / frame.Height;
        float scale = Math.Min(scaleX, scaleY);

        float drawW = frame.Width * scale;
        float drawH = frame.Height * scale;
        float offsetX = destRect.Left + (destRect.Width - drawW) / 2f;
        float offsetY = destRect.Top + (destRect.Height - drawH) / 2f;

        var centredRect = new SKRect(offsetX, offsetY, offsetX + drawW, offsetY + drawH);

        var handle = GCHandle.Alloc(frame.Data, GCHandleType.Pinned);
        try
        {
            using var bmp = new SKBitmap();
            bmp.InstallPixels(
                new SKImageInfo(frame.Width, frame.Height, SKColorType.Bgra8888, SKAlphaType.Premul),
                handle.AddrOfPinnedObject(),
                frame.Stride);

            canvas.DrawBitmap(bmp, centredRect);
        }
        finally
        {
            handle.Free();
        }
    }

    // -------------------------------------------------------------------------
    // Commands — navigation
    // -------------------------------------------------------------------------

    [RelayCommand]
    private async Task GoBack() => await Shell.Current.GoToAsync("..");

    // -------------------------------------------------------------------------
    // Helpers
    // -------------------------------------------------------------------------

    private void StartStream(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            StatusText = "Please enter a valid RTSP URL.";
            return;
        }

        Interlocked.Exchange(ref _currentFrame, null)?.Release();

        StatusText = "Connecting...";
        IsBusy = true;

        _decoder.Start(url);
    }
}