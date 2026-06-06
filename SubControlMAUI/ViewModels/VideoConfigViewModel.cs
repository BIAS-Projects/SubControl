using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using Microsoft.Extensions.Logging;
using SubControlMAUI.Messages;
using SubControlMAUI.Models;
using SubControlMAUI.Services;
using System.Collections.ObjectModel;
using System.Text;
using System.Text.Json;

namespace SubControlMAUI.ViewModels;

public partial class VideoConfigViewModel : BaseViewModel
{
    private readonly IMessenger _messengerService;
    private readonly ILogger<VideoConfigViewModel> _loggerService;
    private readonly INavigationService _navigationService;
    private readonly IAlertService _alertService;
    private readonly TcpSocketService _tcpService;
    private readonly CancellationTokenSource _cts = new();
    public ApplicationStateService AppState { get; }

    // The routing feature name the server uses to identify this client
    private static string Feature => nameof(VideoConfigViewModel) + "CAMERA";

    [ObservableProperty]
    private bool _cameraDetailsDownloaded;

    [ObservableProperty]
    private string _statusText = "";

    private bool _isRebuilding = false;

    // Derives the picker string list from _allProfiles
    private List<string> AllProfileNames =>
        _allProfiles.Select(p => p.StreamPathName).Prepend("Unselected").ToList();

    public ObservableCollection<CameraDevice> Cameras { get; } = new();

    public VideoConfigViewModel(
        IMessenger messengerService,
        ILogger<VideoConfigViewModel> loggerService,
        INavigationService navigationService,
        IAlertService alertService,
        TcpSocketService tcpService,
        ApplicationStateService applicationStateService)
    {
        _messengerService = messengerService;
        _loggerService = loggerService;
        _navigationService = navigationService;
        _alertService = alertService;
        _tcpService = tcpService;
        AppState = applicationStateService;

        Title = "Camera Stream Configuration";

        _messengerService.Register<TcpDataReceivedMessage>(this, async (r, msg) =>
        {
            if (!msg.Value.Function.Equals(Feature)) return;

            switch (msg.Value.Command)
            {
                case "LIST CAMERAS":
                    await HandleListCamerasResponseAsync(msg.Value.Data);
                    break;
                case "LIST REGISTERED":
                    await HandleListRegisteredResponseAsync(msg.Value.Data);
                    break;
                case "REGISTER":
                    await HandleRegisterResponseAsync(msg.Value.Data);
                    break;
                case "UNREGISTER":
                    await HandleUnregisterResponseAsync(msg.Value.Data);
                    break;
                default:
                    await _alertService.ShowAlertAsync("Information",
                        $"TcpDataReceivedMessage: {msg.Value}", "OK");
                    break;
            }
        });

        _messengerService.Register<TcpStatusMessage>(this, (r, msg) =>
        {
            MainThread.BeginInvokeOnMainThread(() => StatusText = msg.Value);
        });

        _messengerService.Register<TcpErrorMessage>(this, (r, msg) =>
        {
            _loggerService.LogError("TcpErrorMessage: {Message}", msg.Value.Message);
            MainThread.BeginInvokeOnMainThread(() => StatusText = msg.Value.Message);
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

    // ── Commands ──────────────────────────────────────────────────────────────

    [RelayCommand]
    public async Task QueryDevices()
    {
        IsBusy = true;
        try
        {
            var command = new TCPMessageBody<string>(Feature, "LIST CAMERAS", "");

            if (!await _tcpService.SendCommandAsync(command, _cts.Token))
            {
                StatusText = "Request failed — no response from server";
                CameraDetailsDownloaded = false;
            }
            // Success is handled in TcpDataReceivedMessage → HandleListCamerasResponseAsync
            // which chains LIST REGISTERED
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    public async Task Save()
    {
        IsBusy = true;
        try
        {
            foreach (var camera in Cameras)
            {
                if (camera.SelectedProfile == "Unselected")
                {
                    var command = new TCPMessageBody<string>(
                        Feature, "UNREGISTER", camera.DeviceId);

                    if (!await _tcpService.SendCommandAsync(command, _cts.Token))
                    {
                        StatusText = $"UNREGISTER failed for {camera.DeviceId}";
                        return;
                    }
                }
                else
                {
                    var profile = _allProfiles.First(p => p.StreamPathName == camera.SelectedProfile);

                    var request = new CameraRegistrationRequest
                    {
                        DeviceId = camera.DeviceId,
                        StreamPathName = profile.StreamPathName,
                        FfmpegOptions = profile.FfmpegOptions,
                        MtxConfig = profile.MtxConfig
                    };

                    var command = new TCPMessageBody<string>(
                        Feature, "REGISTER", JsonSerializer.Serialize(request));

                    if (!await _tcpService.SendCommandAsync(command, _cts.Token))
                    {
                        StatusText = $"REGISTER failed for {camera.DeviceId}";
                        return;
                    }
                }
            }

            StatusText = "Save complete";
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void HandleProfileSelection(CameraDevice changedDevice, string selectedValue)
    {
        if (_isRebuilding) return;
        _isRebuilding = true;
        try
        {
            foreach (var device in Cameras)
            {
                var otherClaimed = Cameras
                    .Where(d => d != device && d.SelectedProfile != "Unselected")
                    .Select(d => d.SelectedProfile)
                    .ToHashSet();

                var available = AllProfileNames
                    .Where(p => p == "Unselected" || !otherClaimed.Contains(p))
                    .ToList();

                var currentSelection = device.SelectedProfile;

                device.UpdateAvailableProfiles(available);
                device.SetSelectionSilently(
                    available.Contains(currentSelection) ? currentSelection : "Unselected");
            }
        }
        finally
        {
            _isRebuilding = false;
        }
    }


    // ── Response handlers ─────────────────────────────────────────────────────

    private async Task HandleListCamerasResponseAsync(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            await MainThread.InvokeOnMainThreadAsync(() =>
                StatusText = "Empty LIST CAMERAS response");
            return;
        }

        try
        {
            var response = JsonSerializer.Deserialize<ListCamerasResponse>(json,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

            if (response is null || !response.Ok)
            {
                await MainThread.InvokeOnMainThreadAsync(() =>
                    StatusText = $"Server error: {response?.Error ?? "unknown"}");
                return;
            }

            await MainThread.InvokeOnMainThreadAsync(() =>
            {
                Cameras.Clear();
                foreach (var cam in response.Data)
                {
                    var device = new CameraDevice
                    {
                        DeviceId = cam.DeviceId,
                        FriendlyName = cam.FriendlyName,
                        OnProfileSelected = HandleProfileSelection
                    };
                    device.UpdateAvailableProfiles(AllProfileNames);
                    device.SetSelectionSilently("Unselected");
                    Cameras.Add(device);
                }

                CameraDetailsDownloaded = true;
                StatusText = $"Found {response.Data.Count} camera(s)";
            });

            // Chain: now fetch registered state to restore stream path names
            var command = new TCPMessageBody<string>(Feature, "LIST REGISTERED", "");
            if (!await _tcpService.SendCommandAsync(command, _cts.Token))
            {
                await MainThread.InvokeOnMainThreadAsync(() =>
                    StatusText = "LIST REGISTERED request failed");
            }
        }
        catch (JsonException ex)
        {
            _loggerService.LogError(ex, "Failed to deserialize LIST CAMERAS response");
            await MainThread.InvokeOnMainThreadAsync(() =>
                StatusText = $"Parse error: {ex.Message}");
        }
    }

    private async Task HandleListRegisteredResponseAsync(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            await MainThread.InvokeOnMainThreadAsync(() =>
                StatusText = "Empty LIST REGISTERED response");
            return;
        }

        try
        {
            var response = JsonSerializer.Deserialize<ListRegisteredCamerasResponse>(json,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

            if (response is null || !response.Ok)
            {
                await MainThread.InvokeOnMainThreadAsync(() =>
                    StatusText = $"LIST REGISTERED error: {response?.Error ?? "unknown"}");
                return;
            }

            await MainThread.InvokeOnMainThreadAsync(() =>
            {
                _isRebuilding = true;
                try
                {
                    foreach (var entry in response.Data)
                    {
                        var camera = Cameras.FirstOrDefault(c => c.DeviceId == entry.DeviceId);
                        if (camera is null) continue;

                        camera.IsRegisteredWithMtx = entry.IsRegisteredWithMtx;

                        if (!string.IsNullOrWhiteSpace(entry.StreamPathName)
                            && AllProfileNames.Contains(entry.StreamPathName))
                        {
                            camera.SetSelectionSilently(entry.StreamPathName);
                        }
                    }
                }
                finally
                {
                    _isRebuilding = false;
                }

                // Rebuild available lists to enforce exclusivity
                if (Cameras.Any())
                    HandleProfileSelection(Cameras[0], Cameras[0].SelectedProfile);

                StatusText = $"Restored {response.Data.Count} registration(s)";
            });
        }
        catch (JsonException ex)
        {
            _loggerService.LogError(ex, "Failed to deserialize LIST REGISTERED response");
            await MainThread.InvokeOnMainThreadAsync(() =>
                StatusText = $"Parse error: {ex.Message}");
        }
    }

    private async Task HandleRegisterResponseAsync(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return;
        try
        {
            var response = JsonSerializer.Deserialize<CommandResponse>(json,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

            await MainThread.InvokeOnMainThreadAsync(() =>
                StatusText = response?.Ok == true
                    ? "Camera registered successfully"
                    : $"REGISTER error: {response?.Error ?? "unknown"}");
        }
        catch (JsonException ex)
        {
            _loggerService.LogError(ex, "Failed to deserialize REGISTER response");
        }
    }

    private async Task HandleUnregisterResponseAsync(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return;
        try
        {
            var response = JsonSerializer.Deserialize<CommandResponse>(json,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

            await MainThread.InvokeOnMainThreadAsync(() =>
                StatusText = response?.Ok == true
                    ? "Camera unregistered successfully"
                    : $"UNREGISTER error: {response?.Error ?? "unknown"}");
        }
        catch (JsonException ex)
        {
            _loggerService.LogError(ex, "Failed to deserialize UNREGISTER response");
        }
    }

    private readonly List<CameraProfile> _allProfiles = new()
{
    new CameraProfile
    {
        StreamPathName = "usbcamera",
        FfmpegOptions = new FfmpegCameraOptions
        {
            DeviceName  = "USB Camera",
            Width       = 1280,
            Height      = 720,
            Framerate   = 30,
            PixelFormat = "yuv420p",
            VideoCodec  = "libx264",
            Preset      = "ultrafast",
            Tune        = "zerolatency",
            Bitrate     = "4M"
        },
        MtxConfig = new MediaMtxPathConfig
        {
            // Start immediately when the path is created
            RunOnInit              = null,   // built server-side from FfmpegOptions
            RunOnInitRestart       = true,   // restart FFmpeg if it crashes

            // Also keep on-demand as a fallback if init stream dies
            RunOnDemandRestart      = true,
            RunOnDemandStartTimeout = "10s",
            RunOnDemandCloseAfter   = "0s",  // never close — keep streaming

            OverridePublisher = true         // allow on-demand to replace init if needed
        }
    },
    new CameraProfile
    {
        StreamPathName = "flir",
        FfmpegOptions = new FfmpegCameraOptions
        {
            DeviceName  = "FLIR Video",
            Width       = 640,
            Height      = 512,
            Framerate   = 9,
            PixelFormat = "yuv420p",
            VideoCodec  = "libx264",
            Preset      = "ultrafast",
            Tune        = "zerolatency",
            Bitrate     = "2M"
        },
        MtxConfig = new MediaMtxPathConfig
        {
            RunOnInit               = null,  // built server-side
            RunOnInitRestart        = true,
            RunOnDemandRestart      = true,
            RunOnDemandStartTimeout = "10s",
            RunOnDemandCloseAfter   = "0s",
            OverridePublisher       = true
        }
    }
};

    //private static FfmpegCameraOptions? GetFfmpegOptions(string friendlyName)
    //    => _cameraProfiles.TryGetValue(friendlyName, out var opts) ? opts : null;



    /// <summary>
    /// FFmpeg capture parameters used to generate the RunOnDemand command.
    /// Stored separately so they can be edited independently of the MTX path settings.
    /// </summary>
    public sealed record FfmpegCameraOptions
    {
        /// <summary>
        /// The device name passed to FFmpeg -i.
        /// Windows: dshow friendly name e.g. "USB Camera".
        /// Linux:   device path e.g. "/dev/video0".
        /// Prefer the SymbolicLink (Windows) or stable sysfs path for resilience on replug.
        /// </summary>
        public string DeviceName { get; init; } = "";

        public int Width { get; init; } = 1280;
        public int Height { get; init; } = 720;
        public int Framerate { get; init; } = 30;

        /// <summary>e.g. "yuv420p". Null lets FFmpeg negotiate with the driver.</summary>
        public string? PixelFormat { get; init; }

        /// <summary>FFmpeg video codec, e.g. "libx264".</summary>
        public string VideoCodec { get; init; } = "libx264";

        /// <summary>x264 preset, e.g. "ultrafast".</summary>
        public string Preset { get; init; } = "ultrafast";

        /// <summary>x264 tune, e.g. "zerolatency".</summary>
        public string Tune { get; init; } = "zerolatency";

        /// <summary>Target bitrate, e.g. "4M".</summary>
        public string Bitrate { get; init; } = "4M";
    }

    /// <summary>Payload for the REGISTER camera command.</summary>
    public sealed class CameraRegistrationRequest
    {
        public string DeviceId { get; init; } = "";
        public string StreamPathName { get; init; } = "";
        public FfmpegCameraOptions FfmpegOptions { get; init; } = new();
        public MediaMtxPathConfig? MtxConfig { get; init; }
    }

    /// <summary>
    /// All configurable fields for a MediaMTX path, grouped to match the
    /// official configuration file reference (v1.18.x).
    /// Null values are omitted when serialising to the API, meaning MediaMTX
    /// will use its own defaults for those fields.
    /// </summary>
    public sealed record MediaMtxPathConfig
    {
        // ── General ──────────────────────────────────────────────────────────────

        /// <summary>
        /// Stream source. For USB cameras this is always "publisher" (FFmpeg pushes in).
        /// Could also be an RTSP/RTMP URL for pull sources.
        /// </summary>
        public string? Source { get; init; }

        /// <summary>TLS fingerprint when pulling from a source with a self-signed cert.</summary>
        public string? SourceFingerprint { get; init; }

        /// <summary>Pull source only when at least one reader is connected.</summary>
        public bool? SourceOnDemand { get; init; }

        /// <summary>How long to wait for a pull source to become ready before failing readers.</summary>
        public string? SourceOnDemandStartTimeout { get; init; }

        /// <summary>Close a pull source after this long with no readers.</summary>
        public string? SourceOnDemandCloseAfter { get; init; }

        /// <summary>Maximum number of simultaneous readers. 0 = unlimited.</summary>
        public int? MaxReaders { get; init; }

        /// <summary>SRT passphrase required to read from this path.</summary>
        public string? SrtReadPassphrase { get; init; }

        /// <summary>Forward absolute frame timestamps rather than replacing them with wall-clock time.</summary>
        public bool? UseAbsoluteTimestamp { get; init; }

        // ── Always-available ─────────────────────────────────────────────────────

        /// <summary>
        /// Play a silent/offline segment on repeat when the stream is not available.
        /// Useful so clients don't disconnect when the camera is briefly offline.
        /// </summary>
        public bool? AlwaysAvailable { get; init; }

        // ── Recording ────────────────────────────────────────────────────────────

        /// <summary>Write incoming stream to disk.</summary>
        public bool? Record { get; init; }

        /// <summary>
        /// Output path template, e.g. "./recordings/%path/%Y-%m-%d_%H-%M-%S-%f".
        /// Supports: %path, %Y, %m, %d, %H, %M, %S, %f, %z, %s.
        /// </summary>
        public string? RecordPath { get; init; }

        /// <summary>Segment container format: "fmp4" (default) or "mpegts".</summary>
        public string? RecordFormat { get; init; }

        /// <summary>Duration of each part within a segment. Equals the RPO on crash.</summary>
        public string? RecordPartDuration { get; init; }

        /// <summary>Maximum part size before flushing (prevents RAM exhaustion).</summary>
        public string? RecordMaxPartSize { get; init; }

        /// <summary>Minimum duration of each recording segment file.</summary>
        public string? RecordSegmentDuration { get; init; }

        /// <summary>Automatically delete segments older than this. "0s" disables deletion.</summary>
        public string? RecordDeleteAfter { get; init; }

        // ── Publisher source settings ─────────────────────────────────────────────

        /// <summary>Allow a new publisher to kick the current one off this path.</summary>
        public bool? OverridePublisher { get; init; }

        /// <summary>SRT passphrase required to publish to this path.</summary>
        public string? SrtPublishPassphrase { get; init; }

        /// <summary>Demux MPEG-TS over RTSP into elementary streams.</summary>
        public bool? RtspDemuxMpegts { get; init; }

        // ── RTSP pull source settings ─────────────────────────────────────────────

        /// <summary>Transport when pulling: "automatic", "udp", "multicast", "tcp".</summary>
        public string? RtspTransport { get; init; }

        /// <summary>
        /// Accept sources that use random or missing server ports.
        /// Security risk — only enable when the source requires it.
        /// </summary>
        public bool? RtspAnyPort { get; init; }

        /// <summary>RTSP Range header type: "clock", "npt", or "smpte".</summary>
        public string? RtspRangeType { get; init; }

        /// <summary>RTSP Range start value (format depends on RtspRangeType).</summary>
        public string? RtspRangeStart { get; init; }

        // ── Hooks ─────────────────────────────────────────────────────────────────

        /// <summary>
        /// Command run when the path is initialised (server start).
        /// Terminated with SIGINT on server shutdown.
        /// Env vars: MTX_PATH, RTSP_PORT.
        /// </summary>
        public string? RunOnInit { get; init; }

        /// <summary>Restart RunOnInit if the process exits.</summary>
        public bool? RunOnInitRestart { get; init; }

        /// <summary>
        /// Command run on-demand when a reader connects and no publisher is active.
        /// This is the primary hook for USB cameras — FFmpeg is launched here.
        /// Env vars: MTX_PATH, MTX_QUERY, RTSP_PORT.
        /// </summary>
        public string? RunOnDemand { get; init; }

        /// <summary>Restart RunOnDemand if the process exits unexpectedly.</summary>
        public bool? RunOnDemandRestart { get; init; }

        /// <summary>Hold readers for up to this long waiting for RunOnDemand to start publishing.</summary>
        public string? RunOnDemandStartTimeout { get; init; }

        /// <summary>Terminate RunOnDemand after this long with no readers.</summary>
        public string? RunOnDemandCloseAfter { get; init; }

        /// <summary>Command run when the last reader disconnects. Env vars same as RunOnDemand.</summary>
        public string? RunOnUnDemand { get; init; }

        /// <summary>
        /// Command run when the stream becomes ready to read.
        /// Env vars: MTX_PATH, MTX_QUERY, MTX_SOURCE_TYPE, MTX_SOURCE_ID, RTSP_PORT.
        /// </summary>
        public string? RunOnReady { get; init; }

        /// <summary>Restart RunOnReady if the process exits.</summary>
        public bool? RunOnReadyRestart { get; init; }

        /// <summary>Command run when the stream stops being ready. Env vars same as RunOnReady.</summary>
        public string? RunOnNotReady { get; init; }

        /// <summary>
        /// Command run when a client starts reading.
        /// Env vars: MTX_PATH, MTX_QUERY, MTX_READER_TYPE, MTX_READER_ID, RTSP_PORT.
        /// </summary>
        public string? RunOnRead { get; init; }

        /// <summary>Restart RunOnRead if the process exits.</summary>
        public bool? RunOnReadRestart { get; init; }

        /// <summary>Command run when a client stops reading. Env vars same as RunOnRead.</summary>
        public string? RunOnUnread { get; init; }

        /// <summary>Command run when a new recording segment file is created.</summary>
        public string? RunOnRecordSegmentCreate { get; init; }

        /// <summary>Command run when a recording segment is complete and closed.</summary>
        public string? RunOnRecordSegmentComplete { get; init; }

        // ── Helpers ───────────────────────────────────────────────────────────────

        /// <summary>
        /// Builds the FFmpeg dshow command string for this camera's RunOnDemand hook.
        /// Requires <see cref="FfmpegCameraOptions"/> to be set on the registration.
        /// </summary>
        public static string BuildDshowFfmpegCommand(FfmpegCameraOptions opts, string mtxPathName)
        {
            var sb = new StringBuilder("ffmpeg");

            if (!string.IsNullOrWhiteSpace(opts.PixelFormat))
                sb.Append($" -pixel_format {opts.PixelFormat}");

            sb.Append($" -f dshow");
            sb.Append($" -video_size {opts.Width}x{opts.Height}");
            sb.Append($" -framerate {opts.Framerate}");
            sb.Append($" -i video=\"{opts.DeviceName}\"");
            sb.Append($" -c:v {opts.VideoCodec}");
            sb.Append($" -preset {opts.Preset}");
            sb.Append($" -tune {opts.Tune}");
            sb.Append($" -g {opts.Framerate}");   // keyframe every second
            sb.Append($" -b:v {opts.Bitrate}");
            sb.Append($" -f rtsp -rtsp_transport tcp");
            sb.Append($" rtsp://localhost:8554/{mtxPathName}");

            return sb.ToString();
        }

        /// <summary>
        /// Builds the FFmpeg v4l2 command string for Linux USB cameras.
        /// </summary>
        public static string BuildV4l2FfmpegCommand(FfmpegCameraOptions opts, string mtxPathName)
        {
            var sb = new StringBuilder("ffmpeg");

            sb.Append($" -f v4l2");
            sb.Append($" -video_size {opts.Width}x{opts.Height}");
            sb.Append($" -framerate {opts.Framerate}");

            if (!string.IsNullOrWhiteSpace(opts.PixelFormat))
                sb.Append($" -pixel_format {opts.PixelFormat}");

            sb.Append($" -i {opts.DeviceName}");   // e.g. /dev/video0
            sb.Append($" -c:v {opts.VideoCodec}");
            sb.Append($" -preset {opts.Preset}");
            sb.Append($" -tune {opts.Tune}");
            sb.Append($" -g {opts.Framerate}");
            sb.Append($" -b:v {opts.Bitrate}");
            sb.Append($" -f rtsp -rtsp_transport tcp");
            sb.Append($" rtsp://localhost:8554/{mtxPathName}");

            return sb.ToString();
        }
    }

    [RelayCommand]
    private async Task GoBack()
    {
        await Shell.Current.GoToAsync("..");
    }


}