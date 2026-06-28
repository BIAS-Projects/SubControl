using Microsoft.Extensions.Logging;
using SubConsole.Models;
using SubConsole.Services.Helpers;
using SubConsole.Services.SQL;
using System.Collections.Concurrent;
using System.Text;

namespace SubConsole.Services.Video;

// ================================================================
//  MediaMTX path configuration — mirrors the full per-path API schema
// ================================================================

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
        sb.Append($" rtsp://127.0.0.1:8554/{mtxPathName}");

        return sb.ToString();
    }
}

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

// ================================================================
//  CameraRegistration — what the registry stores per camera
// ================================================================

public sealed class CameraRegistration
{
    public CameraRegistration(
        UsbCameraInfo camera,
        string streamPathName,
        FfmpegCameraOptions ffmpegOptions,
        MediaMtxPathConfig mtxConfig)
    {
        Camera = camera;
        StreamPathName = streamPathName;
        FfmpegOptions = ffmpegOptions;
        MtxConfig = mtxConfig;
    }

    /// <summary>Hardware info discovered by UsbCameraMapper.</summary>
    public UsbCameraInfo Camera { get; }

    /// <summary>The MediaMTX path name, e.g. "usbcamera" or "flir".</summary>
    public string StreamPathName { get; }

    /// <summary>FFmpeg capture settings used to build the RunOnDemand command.</summary>
    public FfmpegCameraOptions FfmpegOptions { get; set; }

    /// <summary>Full MediaMTX path configuration.</summary>
    public MediaMtxPathConfig MtxConfig { get; set; }

    /// <summary>Whether the path is currently pushed to MediaMTX.</summary>
    public bool IsRegisteredWithMtx { get; set; }
}

// ================================================================
//  ICameraRegistry
// ================================================================

public interface ICameraRegistry
{
    Task<OperationResult> RegisterAsync(
        UsbCameraInfo camera,
        string streamPathName,
        FfmpegCameraOptions ffmpegOptions,
        MediaMtxPathConfig mtxConfig);

    Task<OperationResult> UnregisterAsync(UsbCameraInfo camera);

    Task<OperationResult> LoadFromDatabaseAsync();

    /// <summary>Update the MTX config and/or FFmpeg options for an already-registered camera.</summary>
    Task<OperationResult> UpdateAsync(
        string deviceId,
        FfmpegCameraOptions? ffmpegOptions,
        MediaMtxPathConfig? mtxConfig);

    CameraRegistration? GetByDeviceId(string deviceId);
    CameraRegistration? GetByStreamPath(string streamPathName);

    IReadOnlyList<CameraRegistration> AllRegistrations { get; }
}

// ================================================================
//  CameraRegistry
// ================================================================

public sealed class CameraRegistry : ICameraRegistry
{
    private readonly ILogger<CameraRegistry> _logger;
    private readonly SQLiteService _database;

    // deviceId → registration
    private readonly ConcurrentDictionary<string, CameraRegistration> _byDeviceId = new();

    // stream path name → deviceId  (e.g. "usbcamera" → "USB\VID_1234...")
    private readonly ConcurrentDictionary<string, string> _byStreamPath =
        new(StringComparer.OrdinalIgnoreCase);

    public CameraRegistry(ILogger<CameraRegistry> logger, SQLiteService database)
    {
        _logger = logger;
        _database = database;
    }

    // ── Load ──────────────────────────────────────────────────────────────────

    public async Task<OperationResult> LoadFromDatabaseAsync()
    {
        _logger.LogInformation("Loading camera registry from database");

        var result = await _database.GetCameraRegistrationsAsync();
        if (!result.IsSuccess)
        {
            _logger.LogError(
                "Error loading camera registry: {Message}", result.Message);
            return OperationResult.Failure(result.Message);
        }

        foreach (var reg in result.Value)
            InternalAdd(reg);

        _logger.LogInformation(
            "Completed loading camera registry. Cameras loaded: {Count}", result.Value.Count);
        return OperationResult.Success();
    }

    // ── Registration ──────────────────────────────────────────────────────────

    public async Task<OperationResult> RegisterAsync(
        UsbCameraInfo camera,
        string streamPathName,
        FfmpegCameraOptions ffmpegOptions,
        MediaMtxPathConfig mtxConfig)
    {
        _logger.LogInformation(
            "Registering camera {DeviceId} as stream path '{StreamPath}'",
            camera.DeviceId,
            streamPathName);

        var reg = new CameraRegistration(camera, streamPathName, ffmpegOptions, mtxConfig);

        InternalAdd(reg);

        var result = await _database.UpsertCameraRegistrationAsync(reg);
        if (!result.IsSuccess)
        {
            _logger.LogError(
                "Error saving camera registration {DeviceId} to database: {Message}",
                camera.DeviceId,
                result.Message);
            return OperationResult.Failure(result.Message);
        }

        _logger.LogInformation(
            "Completed registering camera {DeviceId} ('{StreamPath}')",
            camera.DeviceId,
            streamPathName);

        return OperationResult.Success();
    }

    public async Task<OperationResult> UnregisterAsync(UsbCameraInfo camera)
    {
        _logger.LogInformation("Unregistering camera {DeviceId}", camera.DeviceId);

        if (!_byDeviceId.TryRemove(camera.DeviceId, out var reg))
        {
            _logger.LogWarning(
                "Unregister failed for {DeviceId}: not found", camera.DeviceId);
            return OperationResult.Failure("Camera not found in registry");
        }

        _byStreamPath.TryRemove(reg.StreamPathName, out _);

        var result = await _database.DeleteCameraRegistrationAsync(camera.DeviceId);
        if (!result.IsSuccess)
        {
            _logger.LogError(
                "Error deleting camera registration {DeviceId} from database: {Message}",
                camera.DeviceId,
                result.Message);
            return result;
        }

        _logger.LogInformation(
            "Completed unregistering camera {DeviceId}", camera.DeviceId);

        return OperationResult.Success();
    }

    // ── Update ────────────────────────────────────────────────────────────────

    public async Task<OperationResult> UpdateAsync(
        string deviceId,
        FfmpegCameraOptions? ffmpegOptions,
        MediaMtxPathConfig? mtxConfig)
    {
        _logger.LogInformation("Updating camera registration {DeviceId}", deviceId);

        if (!_byDeviceId.TryGetValue(deviceId, out var reg))
        {
            _logger.LogWarning("Update failed for {DeviceId}: not found", deviceId);
            return OperationResult.Failure("Camera not found in registry");
        }

        if (ffmpegOptions is not null)
            reg.FfmpegOptions = ffmpegOptions;

        if (mtxConfig is not null)
            reg.MtxConfig = mtxConfig;

        var result = await _database.UpsertCameraRegistrationAsync(reg);
        if (!result.IsSuccess)
        {
            _logger.LogError(
                "Error saving updated registration {DeviceId}: {Message}",
                deviceId,
                result.Message);
            return result;
        }

        _logger.LogInformation(
            "Completed updating camera registration {DeviceId}", deviceId);

        return OperationResult.Success();
    }

    // ── Lookups ───────────────────────────────────────────────────────────────

    public CameraRegistration? GetByDeviceId(string deviceId)
    {
        if (_byDeviceId.TryGetValue(deviceId, out var reg))
            return reg;

        _logger.LogDebug("GetByDeviceId: no registration for {DeviceId}", deviceId);
        return null;
    }

    public CameraRegistration? GetByStreamPath(string streamPathName)
    {
        if (!_byStreamPath.TryGetValue(streamPathName, out var deviceId))
        {
            _logger.LogDebug("GetByStreamPath: no mapping for '{StreamPath}'", streamPathName);
            return null;
        }

        return GetByDeviceId(deviceId);
    }

    public IReadOnlyList<CameraRegistration> AllRegistrations =>
        _byDeviceId.Values.ToList().AsReadOnly();

    // ── Private helpers ───────────────────────────────────────────────────────

    private void InternalAdd(CameraRegistration reg)
    {
        _byDeviceId[reg.Camera.DeviceId] = reg;
        _byStreamPath[reg.StreamPathName] = reg.Camera.DeviceId;
    }
}