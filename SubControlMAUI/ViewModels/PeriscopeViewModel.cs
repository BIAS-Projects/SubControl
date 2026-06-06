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
    private readonly TcpSocketService _tcpService;
    private readonly IRtspFrameDecoder _decoder;
    public ApplicationStateService AppState { get; }


    // ---------------------------------------------------------------------
    // Frame storage
    // ---------------------------------------------------------------------

    private volatile VideoFrame? _currentFrame;
    private volatile bool _disposed;

    public VideoFrame? CurrentFrame => _currentFrame;

    // Notify view that a repaint is required
    public event Action? FrameInvalidated;

    private readonly IRtspFrameDecoder _decoder2;
    private volatile VideoFrame? _currentFrame2;
    private volatile bool _isDualStream;

    public VideoFrame? CurrentFrame2 => _currentFrame2;
    public bool IsDualStream => _isDualStream;

    // ---------------------------------------------------------------------
    // FPS
    // ---------------------------------------------------------------------

    private int _frameCount;
    private DateTime _fpsTimer = DateTime.UtcNow;

    [ObservableProperty]
    private double fps;

    // ---------------------------------------------------------------------
    // UI state
    // ---------------------------------------------------------------------

    private static string SerialFeature => nameof(PeriscopeViewModel);
    private static string CameraFeature => nameof(PeriscopeViewModel) + "CAMERA";

    private TimeSpan timeout = TimeSpan.FromSeconds(10);


    [ObservableProperty]
    private string statusText = "Stopped";


    [ObservableProperty]
    private string rtspVideoUrl = "";

    [ObservableProperty]
    private string rtspFLIRUrl = "";

    public string MTXRTSPPort { get; set; } = "8554";

    public string VideoEndPoint { get; set; } = "/usbcamera";

    public string FlirEndpoint { get; set; } = "/flir";

    [ObservableProperty]
    private bool isStreaming;

    private TaskCompletionSource<bool>? _pendingCommand;
    private string? _pendingCommandName;


    private TaskCompletionSource<bool>? _pendingPushConfirm;
    private string? _pendingPushConfirmFunction;
    private Func<string, bool>? _pendingPushConfirmPredicate;

    // ---------------------------------------------------------------------
    // Constructor
    // ---------------------------------------------------------------------

    public PeriscopeViewModel(
        SQLiteService sqliteService,
        IAlertService alertService,
        IMessenger messengerService,
        TcpSocketService tcpService,
        ILogger<PeriscopeViewModel> logger,
        IRtspFrameDecoder decoder,
        IRtspFrameDecoder decoder2,
    ApplicationStateService applicationStateService)
    {
        Title = "Periscope";

        _sqliteService = sqliteService;
        _alertService = alertService;
        _messengerService = messengerService;
        _tcpService = tcpService;
        _logger = logger;
        _decoder = decoder;
        AppState = applicationStateService;
        _decoder2 = decoder2;

        StatusText = "";
        ButtonSize = 20.0;


        _messengerService.Register<TcpDataReceivedMessage>(this, async (r, msg) =>
        {
            //Commands
            if (msg.Value.Function.Equals("TOM FLIR"))
            {
                if (msg.Value.Command == _pendingCommandName)
                    await ResolvePendingCommandAsync(msg.Value.Data);
                return;
            }

        });

        _messengerService.Register<TcpSendRequestMessage>(this, (r, msg) =>
        {
            MainThread.BeginInvokeOnMainThread(() =>
            {
                //      Status = Encoding.UTF8.GetString(msg.Value);
            });

        });

        _messengerService.Register<TcpStatusMessage>(this, (r, msg) =>
        {
            MainThread.BeginInvokeOnMainThread(() =>
            {
                StatusText = msg.Value;
            });

        });

        _messengerService.Register<TcpErrorMessage>(this, (r, msg) =>
        {
            _logger?.LogError($"TcpErrorMessage : {msg}", msg);
            MainThread.BeginInvokeOnMainThread(() =>
            {
                StatusText = msg.Value.Message;

            });

        });



        _messengerService.Register<TcpAckTimeoutMessage>(this, (r, msg) =>
        {

            MainThread.BeginInvokeOnMainThread(() =>
                StatusText = $"No response to: {msg.Command}");
        });

        _messengerService.Register<TcpNackMessage>(this, (r, msg) =>
        {
            MainThread.BeginInvokeOnMainThread(() =>
                StatusText = $"Server rejected '{msg.Command}': {msg.Reason}");
        });


        _messengerService.Register<TcpIsConnected>(this, (r, msg) =>
        {
            MainThread.BeginInvokeOnMainThread(async () =>
            {
                if (!msg.Value)
                    await Shell.Current.GoToAsync("//MainPage");
            });
        });


    }



    [ObservableProperty]
    private double buttonSize;

    [ObservableProperty]
    private double layoutSpacing;


    public async Task InitializeAsync()
    {
        try
        {
            IsBusy = true;

            var success = await _sqliteService.GetConfigAsync();

            if (!success)
            {
                StatusText = "Failed to load config";
                return;
            }

            string ipAddress = _sqliteService.config.IPAddress;
            rtspVideoUrl = "rtsp://localhost:8554/usbcamera";
            //= ipAddress + ":" + MTXRTSPPort + VideoEndPoint;
            rtspFLIRUrl = "rtsp://localhost:8554/flir";
                //ipAddress + ":" + MTXRTSPPort + FlirEndpoint;

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




    // ---------------------------------------------------------------------
    // Lifecycle
    // ---------------------------------------------------------------------

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

        var old = Interlocked.Exchange(ref _currentFrame, null);
        old?.Release();
        var old2 = Interlocked.Exchange(ref _currentFrame2, null);
        old2?.Release();
    }

    public void Dispose()
    {
        Stop();
    }

    // ---------------------------------------------------------------------
    // Decoder callbacks
    // ---------------------------------------------------------------------

    private void OnFrameReady(VideoFrame newFrame)
    {
        if (_disposed)
        {
            newFrame.Release();
            return;
        }

        var old = Interlocked.Exchange(ref _currentFrame, newFrame);
        old?.Release();

        _frameCount++;

        var elapsed = (DateTime.UtcNow - _fpsTimer).TotalSeconds;

        if (elapsed >= 1.0)
        {
            var fps = _frameCount / elapsed;

            _frameCount = 0;
            _fpsTimer = DateTime.UtcNow;

            MainThread.BeginInvokeOnMainThread(() =>
            {
                Fps = fps;
            });
        }

        MainThread.BeginInvokeOnMainThread(() =>
        {
            FrameInvalidated?.Invoke();
        });
    }

    private void OnFrameReady2(VideoFrame newFrame)
    {
        if (_disposed) { newFrame.Release(); return; }

        var old = Interlocked.Exchange(ref _currentFrame2, newFrame);
        old?.Release();

        MainThread.BeginInvokeOnMainThread(() => FrameInvalidated?.Invoke());
    }


    private void Decoder_StatusChanged(string status)
    {
        MainThread.BeginInvokeOnMainThread(() =>
        {
            StatusText = status;

            bool live = status.StartsWith("●");

            IsStreaming = live;
            IsBusy = status.StartsWith("Init") ||
                     status.StartsWith("Connect");
        });
    }

    private async void OnErrorOccurred(string error)
    {
        MainThread.BeginInvokeOnMainThread(async () =>
        {
            StatusText = $"Stream Error: {error}";
            IsBusy = false;
            IsStreaming = false;

        });
    }

    // ---------------------------------------------------------------------
    // Commands
    // ---------------------------------------------------------------------

    [RelayCommand]
    private void DisplayDualVideo()
    {
        _isDualStream = true;

        var old = Interlocked.Exchange(ref _currentFrame, null);
        old?.Release();
        var old2 = Interlocked.Exchange(ref _currentFrame2, null);
        old2?.Release();

        StatusText = "Connecting dual stream...";
        IsBusy = true;

        _decoder.Start(rtspVideoUrl);
        _decoder2.Start(rtspFLIRUrl);
    }

    [RelayCommand]
    private async Task SetFlirWhitehot()
    {



        IsBusy = true;
        try
        {
            StatusText = "Attempting to set FLIR to White hot...";
            if (!await SendAndWaitAsync("TOM FLIR", "WRITE TEXT", FLIR.LUTtoWHITEHOT, timeout))
            {
                MainThread.BeginInvokeOnMainThread(() =>
                {
                    StatusText = "Update failed — could not set colour palette";
                    return;
                });
            }

            StatusText = "FLIR set to White hot";
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task SetFlirRainbow()
    {
        IsBusy = true;
        try
        {
            StatusText = "Attempting to set FLIR to Rainbow...";
            if (!await SendAndWaitAsync("TOM FLIR", "WRITE TEXT", FLIR.LUTtoRAINBOW, timeout))
            {
                MainThread.BeginInvokeOnMainThread(() =>
                {
                    StatusText = "Update failed — could not set colour palette";
                    return;
                });


            }
            StatusText = "FLIR set to Rainbow";
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task SetFlirBlackhot()
    {
        IsBusy = true;
        try
        {
            StatusText = "Attempting to set FLIR to Black hot...";
            if (!await SendAndWaitAsync("TOM FLIR", "WRITE TEXT", FLIR.LUTtoBLACKHOT, timeout))
            {
                MainThread.BeginInvokeOnMainThread(() =>
                {
                    StatusText = "Update failed — could not set colour palette";
                    return;
                });


            }
            StatusText = "FLIR set to Black hot";
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task SetFlirIronbow()
    {
        IsBusy = true;
        try
        {
            StatusText = "Attempting to set FLIR to Iron bow...";
            if (!await SendAndWaitAsync("TOM FLIR", "WRITE TEXT", FLIR.LUTtoIRONBOW, timeout))
            {
                MainThread.BeginInvokeOnMainThread(() =>
                {
                    StatusText = "Update failed — could not set colour palette";
                    return;
                });


            }
            StatusText = "FLIR set to Iron bow";
        }
        finally
        {
            IsBusy = false;
        }
    }


    [RelayCommand]
    private async Task SetFlirGlowbow()
    {
        IsBusy = true;
        try
        {
            StatusText = "Attempting to set FLIR to Glow bow...";
            if (!await SendAndWaitAsync("TOM FLIR", "WRITE TEXT", FLIR.LUTtoGLOBOW, timeout))
            {
                MainThread.BeginInvokeOnMainThread(() =>
                {
                    StatusText = "Update failed — could not set colour palette";
                    return;
                });


            }
            StatusText = "FLIR set to Glow bow";
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private void DisplayStandardVideo()
    {
        _isDualStream = false;
        _decoder2.Stop();
        StartStream(rtspVideoUrl);
    }


    [RelayCommand]
    private void DisplayFLIRVideo()
    {
        _isDualStream = false;
        _decoder2.Stop();
        StartStream(rtspFLIRUrl);
    }



    [RelayCommand]
    private void StopVideo()
    {
        _isDualStream = false;
        _decoder.Stop();
        _decoder2.Stop();

        var old = Interlocked.Exchange(ref _currentFrame, null);
        old?.Release();
        var old2 = Interlocked.Exchange(ref _currentFrame2, null);
        old2?.Release();

        MainThread.BeginInvokeOnMainThread(() => Fps = 0);
        FrameInvalidated?.Invoke();
    }

    [RelayCommand]
    private async Task TakeSnapShot()
    {
        StatusText = "Taking Snapshot...";
        IsBusy = true;

        try
        {
            if (_isDualStream)
            {
                var frame1 = _currentFrame;
                var frame2 = _currentFrame2;

                if (frame1 is null && frame2 is null)
                    return;

                try
                {
                    string path = Path.Combine(
                      //  Environment.GetFolderPath(Environment.SpecialFolder.MyPictures),
                        AppState.SnapShotPath,
                        $"snap_dual_{DateTime.Now:yyyyMMdd_HHmmss}.png");

                    // Use whichever frame is available for dimensions
                    int w1 = frame1?.Width ?? 0, h1 = frame1?.Height ?? 0;
                    int w2 = frame2?.Width ?? 0, h2 = frame2?.Height ?? 0;

                    int totalWidth = w1 + w2;
                    int totalHeight = Math.Max(h1, h2);

                    if (totalWidth <= 0 || totalHeight <= 0)
                        return;

                    var compositeInfo = new SKImageInfo(
                        totalWidth, totalHeight,
                        SKColorType.Bgra8888, SKAlphaType.Premul);

                    using var surface = SKSurface.Create(compositeInfo);
                    var canvas = surface.Canvas;
                    canvas.Clear(SKColors.Black);

                    // Draw frame1 on the left
                    if (frame1 is not null)
                        DrawFrameToCanvas(canvas, frame1,
                            new SKRect(0, 0, w1, totalHeight));

                    // Draw frame2 on the right
                    if (frame2 is not null)
                        DrawFrameToCanvas(canvas, frame2,
                            new SKRect(w1, 0, w1 + w2, totalHeight));

                    using var image = surface.Snapshot();
                    using var png = image.Encode(SKEncodedImageFormat.Png, 95);
                    await using var fs = File.OpenWrite(path);
                    png.SaveTo(fs);

                    StatusText = "Dual Snapshot Saved";
                }
                catch (Exception ex)
                {
                    StatusText = "Snapshot Failed";
                }
            }
            else
            {
                // Original single-stream snapshot
                var frame = _currentFrame;
                if (frame is null) return;

                try
                {
                    string path = Path.Combine(
                                               // Environment.GetFolderPath(Environment.SpecialFolder.MyPictures),
                         AppState.SnapShotPath,
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

                    StatusText = "Snapshot Saved";
                }
                catch (Exception ex)
                {
                    StatusText = "Snapshot Failed";
                }
            }
        }
        finally
        {
            IsBusy = false;
        }
    }

    // Shared by TakeSnapShot — mirrors the letterboxing logic in PeriscopePage.xaml.cs
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

    // ---------------------------------------------------------------------
    // Helpers
    // ---------------------------------------------------------------------

    private void StartStream(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            StatusText = "Please enter a valid RTSP URL.";
            return;
        }

        var old = Interlocked.Exchange(ref _currentFrame, null);
        old?.Release();

        StatusText = "Connecting...";
        IsBusy = true;

        _decoder.Start(url);
    }

    private async Task<bool> SendAndWaitAsync(
     string feature, string command, string data, TimeSpan timeout)
    {
        _pendingCommandName = command;
        var tcs = new TaskCompletionSource<bool>();
        Interlocked.Exchange(ref _pendingCommand, tcs);  // atomic set

        var sent = await _tcpService.SendCommandAsync(
            new TCPMessageBody<string>(feature, command, data), CancellationToken.None);

        if (!sent)
        {
            Interlocked.Exchange(ref _pendingCommand, null);  // atomic clear
            _pendingCommandName = null;
            return false;
        }

        var completed = await Task.WhenAny(tcs.Task, Task.Delay(timeout));

        Interlocked.Exchange(ref _pendingCommand, null);  // atomic clear
        _pendingCommandName = null;

        return completed == tcs.Task && tcs.Task.Result;
    }

    private async Task<bool> SendAndWaitForPushAsync(
        string sendFeature, string sendCommand, string sendData,
        string confirmFunction, Func<string, bool> confirmPredicate,
        TimeSpan timeout)
    {
        _pendingPushConfirmFunction = confirmFunction;
        _pendingPushConfirmPredicate = confirmPredicate;
        var confirmTcs = new TaskCompletionSource<bool>();
        Interlocked.Exchange(ref _pendingPushConfirm, confirmTcs);

        var sent = await _tcpService.SendCommandAsync(
            new TCPMessageBody<string>(sendFeature, sendCommand, sendData),
            CancellationToken.None);

        if (!sent)
        {
            Interlocked.Exchange(ref _pendingPushConfirm, null);
            _pendingPushConfirmFunction = null;
            _pendingPushConfirmPredicate = null;
            return false;
        }

        var completed = await Task.WhenAny(confirmTcs.Task, Task.Delay(timeout));

        Interlocked.Exchange(ref _pendingPushConfirm, null);
        _pendingPushConfirmFunction = null;
        _pendingPushConfirmPredicate = null;

        return completed == confirmTcs.Task && confirmTcs.Task.Result;
    }

    private async Task ResolvePendingCommandAsync(string? json)
    {
        // Capture atomically — local copy is thread-safe from this point on
        var pending = Interlocked.CompareExchange(ref _pendingCommand, null, null);
        if (pending is null) return;

        try
        {
            var response = JsonSerializer.Deserialize<CommandResponse>(
                json ?? "",
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

            pending.TrySetResult(response?.Ok == true);
        }
        catch
        {
            pending.TrySetResult(false);
        }
    }


    [RelayCommand]
    private async Task GoBack()
    {
        await Shell.Current.GoToAsync("..");
    }


}