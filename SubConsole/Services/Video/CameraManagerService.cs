using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SubConsole.Models;
using SubConsole.Services.Helpers;
using SubConsole.Services.Video;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SubConsole.Services.Video;

// ═════════════════════════════════════════════════════════════════════════════
// MediaMTX connection options  — bind in DI via appsettings.json / env vars
// ═════════════════════════════════════════════════════════════════════════════

/// <summary>
/// Configures how <see cref="MediaMtxClient"/> reaches the MediaMTX REST API.
///
/// Bind via <c>appsettings.json</c>:
/// <code>
/// "MediaMtx": {
///   "BaseUrl": "http://127.0.0.1:9997/"
/// }
/// </code>
/// Or override per-environment with an env var:
/// <c>MediaMtx__BaseUrl=http://192.168.1.10:9997/</c>
///
/// IMPORTANT — the URL MUST end with a trailing slash so that
/// <see cref="HttpClient"/> relative paths resolve correctly.
/// </summary>
public sealed class MediaMtxOptions
{
    public const string Section = "MediaMtx";

    /// <summary>
    /// Base URL of the MediaMTX HTTP API, e.g. <c>http://127.0.0.1:9997/</c>.
    /// Defaults to the MediaMTX out-of-the-box address on loopback.
    /// Using 127.0.0.1 instead of "localhost" avoids IPv6/IPv4 resolution
    /// differences between Windows and Linux (on Linux, "localhost" can
    /// resolve to ::1 when MediaMTX is only listening on 0.0.0.0).
    /// </summary>
    public string BaseUrl { get; set; } = "http://127.0.0.1:9997/";
}

// ═════════════════════════════════════════════════════════════════════════════
// Public surface
// ═════════════════════════════════════════════════════════════════════════════

public interface ICameraManagerService
{
    // ── USB enumeration ───────────────────────────────────────────────────────
    Task<IReadOnlyList<UsbCameraInfo>> EnumerateCamerasAsync(
        CancellationToken token = default);

    // ── Registry ──────────────────────────────────────────────────────────────
    Task<OperationResult> RegisterCameraAsync(
        UsbCameraInfo camera,
        string streamPathName,
        FfmpegCameraOptions ffmpegOptions,
        MediaMtxPathConfig mtxConfig,
        CancellationToken token = default);

    Task<OperationResult> UnregisterCameraAsync(
        string deviceId,
        CancellationToken token = default);

    IReadOnlyList<CameraRegistration> GetRegisteredCameras();

    // ── Stream lifecycle ──────────────────────────────────────────────────────

    /// <summary>
    /// Pushes the camera's path configuration to MediaMTX via the API.
    /// FFmpeg will be launched by MediaMTX on first reader connection (runOnDemand).
    /// </summary>
    Task<OperationResult> AddStreamAsync(
        string deviceId,
        CancellationToken token = default);

    /// <summary>
    /// Removes the path from MediaMTX, terminating any active FFmpeg process.
    /// </summary>
    Task<OperationResult> RemoveStreamAsync(
        string deviceId,
        CancellationToken token = default);

    // ── Config management ─────────────────────────────────────────────────────
    Task<OperationResult> UpdateFfmpegOptionsAsync(
        string deviceId,
        FfmpegCameraOptions ffmpegOptions,
        CancellationToken token = default);

    Task<OperationResult> UpdateMtxConfigAsync(
        string deviceId,
        MediaMtxPathConfig mtxConfig,
        CancellationToken token = default);

    // ── Auto-discovery ────────────────────────────────────────────────────────
    Task<OperationResultWithValue<int>> AutoDiscoverAsync(
        bool autoAddToMtx,
        CancellationToken token = default);

    // ── Hotplug ───────────────────────────────────────────────────────────────
    Task<CameraRemapResult> HandleCameraChangedAsync(
        CameraChangedEventArgs e,
        CancellationToken token = default);

    // ── Command dispatcher ────────────────────────────────────────────────────
    Task<OperationResult> ExecuteAsync(
        ICameraCommand command,
        CancellationToken token = default);

    // ── Tests ─────────────────────────────────────────────────────────────────
    Task<OperationResultWithValue<string>> CheckFfmpegAsync(
        CancellationToken token = default);

    Task<OperationResultWithValue<string>> GetMediaMtxVersionAsync(
        CancellationToken token = default);

    Task<OperationResult> CheckMediaMtxStreamsAsync(
        CancellationToken token = default);
}

// ═════════════════════════════════════════════════════════════════════════════
// Command pattern
// ═════════════════════════════════════════════════════════════════════════════

public interface ICameraCommand
{
    Task<OperationResult> ExecuteAsync(
        ICameraManagerService manager,
        CancellationToken token = default);
}

// ═════════════════════════════════════════════════════════════════════════════
// Result type
// ═════════════════════════════════════════════════════════════════════════════

public sealed record CameraRemapResult(
    string? DeviceId,
    string? StreamPathName,
    CameraChangeKind Kind,
    string? Error)
{
    public bool IsSuccess => Error is null;
    public static CameraRemapResult NoOp() =>
        new(null, null, CameraChangeKind.Removed, null);
}

// ═════════════════════════════════════════════════════════════════════════════
// MediaMTX HTTP client  — thin wrapper over the v3 REST API
// ═════════════════════════════════════════════════════════════════════════════

public sealed class MediaMtxClient
{
    private readonly HttpClient _http;
    private readonly ILogger<MediaMtxClient> _logger;
    private readonly JsonSerializerOptions _jsonOpts;

    public MediaMtxClient(HttpClient http, ILogger<MediaMtxClient> logger)
    {
        _http = http;
        _logger = logger;
        _jsonOpts = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            WriteIndented = false
        };

        // Guard: HttpClient.BaseAddress must end with '/' for relative URLs to
        // resolve correctly. HttpClient silently drops the last path segment of
        // the base address when it does NOT end in '/', which causes every API
        // call to hit the wrong endpoint and produces connection-refused / 404
        // errors that are hard to diagnose.
        if (_http.BaseAddress is null)
        {
            throw new InvalidOperationException(
                "MediaMtxClient requires HttpClient.BaseAddress to be set. " +
                "Register it in DI: services.AddHttpClient<MediaMtxClient>(c => " +
                "c.BaseAddress = new Uri(options.BaseUrl)) " +
                "and ensure MediaMtxOptions.BaseUrl ends with '/'.");
        }

        if (!_http.BaseAddress.OriginalString.EndsWith('/'))
        {
            // Fix it rather than throw — one trailing slash won't hurt
            _http.BaseAddress = new Uri(_http.BaseAddress.OriginalString + "/");

            _logger.LogWarning(
                "MediaMtxClient: BaseAddress did not end with '/'. " +
                "Corrected to {BaseAddress}. Update MediaMtxOptions.BaseUrl to suppress this warning.",
                _http.BaseAddress);
        }

        _logger.LogInformation(
            "MediaMtxClient initialised. API base: {BaseAddress}", _http.BaseAddress);
    }

    /// <summary>POST /v3/config/paths/add/{name}</summary>
    public async Task<OperationResult> AddPathAsync(
        string pathName, MediaMtxPathConfig config, CancellationToken token)
    {
        _logger.LogInformation("Adding MediaMTX path '{PathName}'", pathName);
        return await SendAsync(HttpMethod.Post,
            $"v3/config/paths/add/{pathName}", config, token);
    }

    /// <summary>PATCH /v3/config/paths/patch/{name}</summary>
    public async Task<OperationResult> PatchPathAsync(
        string pathName, MediaMtxPathConfig config, CancellationToken token)
    {
        _logger.LogInformation("Patching MediaMTX path '{PathName}'", pathName);
        return await SendAsync(HttpMethod.Patch,
            $"v3/config/paths/patch/{pathName}", config, token);
    }

    /// <summary>GET /v3/info — returns the server version string.</summary>
    public async Task<OperationResultWithValue<string>> GetVersionAsync(
        CancellationToken token = default)
    {
        var result = await SendAsyncWithResponse(HttpMethod.Get, "v3/info", null, token);

        if (!result.IsSuccess)
            return OperationResultWithValue<string>.Failure(result.Message);

        try
        {
            using var doc = JsonDocument.Parse(result.Value!);
            var version = doc.RootElement.GetProperty("version").GetString();
            return OperationResultWithValue<string>.Success(version ?? "unknown");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to parse MediaMTX version response");
            return OperationResultWithValue<string>.Failure("Invalid version response");
        }
    }

    /// <summary>GET /v3/paths/list — returns all currently registered paths.</summary>
    /// <summary>GET /v3/paths/list — returns paths only if they are present and actively running.</summary>
    public async Task<OperationResultWithValue<IReadOnlyList<MediaMtxPathItem>>> GetPathsAsync(
        CancellationToken token = default)
    {
        _logger.LogDebug("Fetching MediaMTX paths");

        var result = await SendAsyncWithResponse(HttpMethod.Get, "v3/paths/list", null, token);

        if (!result.IsSuccess)
            return OperationResultWithValue<IReadOnlyList<MediaMtxPathItem>>.Failure(result.Message);

        try
        {
            var response = JsonSerializer.Deserialize<MediaMtxPathListResponse>(result.Value!, _jsonOpts);
            var items = response?.Items ?? new List<MediaMtxPathItem>();

            // 1. Check if any cameras/paths are present
            if (items.Count == 0)
            {
                _logger.LogWarning("MediaMTX paths check failed: No cameras are present.");
                return OperationResultWithValue<IReadOnlyList<MediaMtxPathItem>>.Failure("Cameras not present");
            }

            // 2. Check if all paths (or at least one, depending on your business logic) are actively running
            // Using .Any() here assumes you want to fail if *any* of them are down. 
            // Swap to .All(x => !x.Ready) if you only want to fail if *all* of them are down.
            bool anyCameraNotRunning = items.Any(x => !x.Ready);

            if (anyCameraNotRunning)
            {
                _logger.LogWarning("MediaMTX paths check failed: One or more paths are configured but not streaming.");
                return OperationResultWithValue<IReadOnlyList<MediaMtxPathItem>>.Failure("Cameras not running");
            }

            _logger.LogDebug("MediaMTX returned {Count} paths, all are running successfully.", items.Count);
            return OperationResultWithValue<IReadOnlyList<MediaMtxPathItem>>.Success(items);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to parse MediaMTX paths response");
            return OperationResultWithValue<IReadOnlyList<MediaMtxPathItem>>.Failure("Invalid paths response");
        }
    }


    /// <summary>DELETE /v3/config/paths/delete/{name}</summary>
    public async Task<OperationResult> DeletePathAsync(
        string pathName, CancellationToken token)
    {
        _logger.LogInformation("Deleting MediaMTX path '{PathName}'", pathName);
        try
        {
            var response = await _http.DeleteAsync(
                $"v3/config/paths/delete/{pathName}", token);

            return response.IsSuccessStatusCode
                ? OperationResult.Success()
                : OperationResult.Failure(
                    $"MediaMTX DELETE returned {(int)response.StatusCode}");
        }
        catch (HttpRequestException ex)
        {
            // Provide a clear diagnosis for the most common Linux failure mode
            _logger.LogError(ex,
                "MediaMTX DELETE path '{PathName}' failed — is MediaMTX running at {BaseAddress}?",
                pathName, _http.BaseAddress);

            return OperationResult.Failure(BuildConnectionErrorMessage(ex));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "MediaMTX DELETE path '{PathName}' threw", pathName);
            return OperationResult.Failure(ex.Message);
        }
    }

    // ── Private helpers ───────────────────────────────────────────────────────

    /// <summary>
    /// Sends an HTTP request with a <see cref="MediaMtxPathConfig"/> body and
    /// returns a plain success/failure result.
    ///
    /// Root cause of the Linux "connection refused" bug:
    ///   On Linux, "localhost" can resolve to the IPv6 loopback (::1) when
    ///   MediaMTX is only bound to 0.0.0.0 (IPv4). Using 127.0.0.1 in
    ///   <see cref="MediaMtxOptions.BaseUrl"/> avoids this entirely.
    ///   This method also catches <see cref="HttpRequestException"/> separately
    ///   so the log message explicitly names the base address, making it
    ///   immediately obvious when the URL is wrong.
    /// </summary>
    private async Task<OperationResult> SendAsync(
        HttpMethod method,
        string url,
        MediaMtxPathConfig config,
        CancellationToken token)
    {
        try
        {
            var json = JsonSerializer.Serialize(config, _jsonOpts);
            var content = new StringContent(json, Encoding.UTF8, "application/json");
            var request = new HttpRequestMessage(method, url) { Content = content };

            _logger.LogDebug(
                "MediaMTX → {Method} {BaseAddress}{Url}",
                method, _http.BaseAddress, url);

            var response = await _http.SendAsync(request, token);

            if (!response.IsSuccessStatusCode)
            {
                var body = await response.Content.ReadAsStringAsync(token);
                _logger.LogWarning(
                    "MediaMTX {Method} {Url} returned {Status}: {Body}",
                    method, url, (int)response.StatusCode, body);
                return OperationResult.Failure(
                    $"MediaMTX {method} {url} returned {(int)response.StatusCode}: {body}");
            }

            return OperationResult.Success();
        }
        catch (HttpRequestException ex)
        {
            // Separate catch so the error message names the full URL and gives
            // actionable advice — "connection refused at http://127.0.0.1:9997/"
            // is far easier to diagnose than a bare socket error.
            _logger.LogError(ex,
                "MediaMTX {Method} {Url} — connection error to {BaseAddress}. " +
                "Ensure MediaMTX is running and MediaMtxOptions.BaseUrl is correct " +
                "(use 127.0.0.1, not 'localhost', to avoid IPv6 resolution on Linux).",
                method, url, _http.BaseAddress);

            return OperationResult.Failure(BuildConnectionErrorMessage(ex));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "MediaMTX {Method} {Url} threw", method, url);
            return OperationResult.Failure(ex.Message);
        }
    }

    /// <summary>
    /// Sends an HTTP request and returns the raw response body string on success.
    /// Used for GET endpoints that return a JSON payload (version, paths list).
    /// </summary>
    private async Task<OperationResultWithValue<string?>> SendAsyncWithResponse(
        HttpMethod method,
        string url,
        object? body,
        CancellationToken token)
    {
        try
        {
            HttpContent? content = null;

            if (body is not null)
            {
                var json = JsonSerializer.Serialize(body, _jsonOpts);
                content = new StringContent(json, Encoding.UTF8, "application/json");
            }

            var request = new HttpRequestMessage(method, url) { Content = content };

            _logger.LogDebug(
                "MediaMTX → {Method} {BaseAddress}{Url}",
                method, _http.BaseAddress, url);

            var response = await _http.SendAsync(request, token);
            var responseBody = await response.Content.ReadAsStringAsync(token);

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning(
                    "MediaMTX {Method} {Url} returned {Status}: {Body}",
                    method, url, (int)response.StatusCode, responseBody);

                return OperationResultWithValue<string?>.Failure(
                    $"MediaMTX {method} {url} returned {(int)response.StatusCode}: {responseBody}");
            }

            return OperationResultWithValue<string?>.Success(responseBody);
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex,
                "MediaMTX {Method} {Url} — connection error to {BaseAddress}. " +
                "Ensure MediaMTX is running and MediaMtxOptions.BaseUrl is correct " +
                "(use 127.0.0.1, not 'localhost', to avoid IPv6 resolution on Linux).",
                method, url, _http.BaseAddress);

            return OperationResultWithValue<string?>.Failure(BuildConnectionErrorMessage(ex));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "MediaMTX {Method} {Url} threw", method, url);
            return OperationResultWithValue<string?>.Failure(ex.Message);
        }
    }

    /// <summary>
    /// Builds a human-readable error message for network failures that names
    /// the configured base address so operators know immediately where to look.
    /// </summary>
    private string BuildConnectionErrorMessage(HttpRequestException ex)
    {
        var inner = ex.InnerException?.Message ?? ex.Message;
        return $"Cannot reach MediaMTX at {_http.BaseAddress}. " +
               $"Check that MediaMTX is running and that MediaMtxOptions.BaseUrl is correct. " +
               $"Tip: use 'http://127.0.0.1:9997/' rather than 'http://localhost:9997/' " +
               $"to avoid IPv6/IPv4 resolution differences on Linux. ({inner})";
    }
}

// ═════════════════════════════════════════════════════════════════════════════
// Implementation
// ═════════════════════════════════════════════════════════════════════════════

public sealed class CameraManagerService : BackgroundService, ICameraManagerService
{
    private readonly ILogger<CameraManagerService> _logger;
    private readonly ICameraRegistry _registry;
    private readonly MediaMtxClient _mtx;

    // deviceId → true (just tracks which paths are currently live in MTX)
    private readonly ConcurrentDictionary<string, bool> _activePaths = new();

    private readonly SemaphoreSlim _remapLock = new(1, 1);

    public CameraManagerService(
        ILogger<CameraManagerService> logger,
        ICameraRegistry registry,
        MediaMtxClient mtx)
    {
        _logger = logger;
        _registry = registry;
        _mtx = mtx;
    }

    // ── BackgroundService entry point ─────────────────────────────────────────

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Starting camera manager service");

        var result = await _registry.LoadFromDatabaseAsync();
        if (!result.IsSuccess)
        {
            _logger.LogError(
                "Error loading camera registry: {Message}", result.Message);
        }
        else
        {
            _logger.LogInformation("Completed loading camera registry");
        }

        try { await Task.Delay(Timeout.Infinite, stoppingToken); }
        catch (OperationCanceledException) { }
    }

    // ── USB enumeration ───────────────────────────────────────────────────────

    public async Task<IReadOnlyList<UsbCameraInfo>> EnumerateCamerasAsync(
        CancellationToken token = default)
        => await UsbCameraMapper.GetUsbCamerasAsync(token);

    // ── Registry ──────────────────────────────────────────────────────────────

    public async Task<OperationResult> RegisterCameraAsync(
        UsbCameraInfo camera,
        string streamPathName,
        FfmpegCameraOptions ffmpegOptions,
        MediaMtxPathConfig mtxConfig,
        CancellationToken token = default)
    {
        _logger.LogInformation(
            "Registering camera {DeviceId} as stream '{StreamPath}'",
            camera.DeviceId, streamPathName);

        return await _registry.RegisterAsync(camera, streamPathName, ffmpegOptions, mtxConfig);
    }

    public async Task<OperationResult> UnregisterCameraAsync(
        string deviceId, CancellationToken token = default)
    {
        _logger.LogInformation("Unregistering camera {DeviceId}", deviceId);

        var remove = await RemoveStreamAsync(deviceId, token);
        if (!remove.IsSuccess)
        {
            _logger.LogWarning(
                "RemoveStream failed during unregister {DeviceId}: {Message}",
                deviceId, remove.Message);
        }

        var reg = _registry.GetByDeviceId(deviceId);
        if (reg is null)
        {
            _logger.LogWarning("Unregister: camera {DeviceId} not found", deviceId);
            return OperationResult.Failure($"Camera {deviceId} not found in registry");
        }

        return await _registry.UnregisterAsync(reg.Camera);
    }

    public IReadOnlyList<CameraRegistration> GetRegisteredCameras()
        => _registry.AllRegistrations;

    // ── Stream lifecycle ──────────────────────────────────────────────────────

    public async Task<OperationResult> AddStreamAsync(
        string deviceId, CancellationToken token = default)
    {
        _logger.LogInformation("Adding stream for camera {DeviceId}", deviceId);

        var reg = _registry.GetByDeviceId(deviceId);
        if (reg is null)
            return OperationResult.Failure($"Camera {deviceId} is not registered");

        if (_activePaths.ContainsKey(deviceId))
            return OperationResult.Failure(
                $"Stream '{reg.StreamPathName}' is already active in MediaMTX");

        var command = RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
            ? MediaMtxPathConfig.BuildDshowFfmpegCommand(reg.FfmpegOptions, reg.StreamPathName)
            : MediaMtxPathConfig.BuildV4l2FfmpegCommand(reg.FfmpegOptions, reg.StreamPathName);

        //var configWithCommand = reg.MtxConfig with
        //{
        //    RunOnInit = command,
        //    RunOnDemand = command
        //};
        var configWithCommand = reg.MtxConfig with
        {
            RunOnInit = command,
            RunOnDemand = string.Empty // Keep empty to avoid process crashing
        };

        var result = await _mtx.AddPathAsync(reg.StreamPathName, configWithCommand, token);
        if (!result.IsSuccess)
        {
            _logger.LogError(
                "MediaMTX AddPath failed for '{StreamPath}': {Message}",
                reg.StreamPathName, result.Message);
            return result;
        }

        _activePaths[deviceId] = true;

        await _registry.UpdateAsync(deviceId, null, configWithCommand);
        reg.IsRegisteredWithMtx = true;

        _logger.LogInformation(
            "Completed adding stream '{StreamPath}' for camera {DeviceId}",
            reg.StreamPathName, deviceId);

        return OperationResult.Success();
    }

    // ── Diagnostics ───────────────────────────────────────────────────────────

    public async Task<OperationResultWithValue<string>> CheckFfmpegAsync(
        CancellationToken token = default)
    {
        try
        {
            // On Linux the binary is just "ffmpeg"; on Windows "ffmpeg.exe"
            // (though Process resolves .exe automatically on Windows too).
            var fileName = RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
                ? "ffmpeg.exe"
                : "ffmpeg";

            var psi = new ProcessStartInfo
            {
                FileName = fileName,
                Arguments = "-version",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true   // no-op on Linux, harmless
            };

            using var process = Process.Start(psi);
            if (process is null)
                return OperationResultWithValue<string>.Failure(
                    "Failed to start ffmpeg process");

            // Read both streams concurrently to prevent pipe-buffer deadlocks
            // on both Windows and Linux (Linux pipes have a smaller default
            // buffer so this matters more there).
            var outputTask = process.StandardOutput.ReadToEndAsync(token);
            var errorTask = process.StandardError.ReadToEndAsync(token);

            await process.WaitForExitAsync(token);

            var fullOutput = await outputTask;
            var fullError = await errorTask;

            if (process.ExitCode != 0)
            {
                return OperationResultWithValue<string>.Failure(
                    $"ffmpeg exited with code {process.ExitCode}: {fullError}");
            }

            var firstLine = !string.IsNullOrWhiteSpace(fullOutput)
                ? fullOutput.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None)[0]
                : "ffmpeg OK";

            return OperationResultWithValue<string>.Success(firstLine);
        }
        catch (Exception ex)
        {
            return OperationResultWithValue<string>.Failure(
                $"ffmpeg not found or failed to execute: {ex.Message}");
        }
    }

    public async Task<OperationResultWithValue<string>> GetMediaMtxVersionAsync(
        CancellationToken token = default)
    {
        _logger.LogInformation("Checking MediaMTX version");

        var result = await _mtx.GetVersionAsync(token);

        if (!result.IsSuccess)
        {
            _logger.LogError("MediaMTX version check failed: {Message}", result.Message);
            return result;
        }

        _logger.LogInformation(
            "MediaMTX is reachable (version {Version})", result.Value);

        return result;
    }

    public async Task<OperationResult> CheckMediaMtxStreamsAsync(
        CancellationToken token = default)
    {
        _logger.LogInformation("Checking MediaMTX stream health");

        var pathsResult = await _mtx.GetPathsAsync(token);

        if (!pathsResult.IsSuccess)
        {
            _logger.LogError(
                "MediaMTX paths check failed: {Message}", pathsResult.Message);
            return OperationResult.Failure(pathsResult.Message);
        }

        var mtxPaths = pathsResult.Value!;
        var expected = _registry.AllRegistrations
            .Where(r => r.IsRegisteredWithMtx)
            .ToList();

        if (expected.Count == 0)
        {
            _logger.LogDebug("No registered MTX streams to validate");
            return OperationResult.Success();
        }

        var missing = new List<string>();
        var notReady = new List<string>();
        var noReaders = new List<string>();

        foreach (var reg in expected)
        {
            var path = mtxPaths.FirstOrDefault(p =>
                string.Equals(p.Name, reg.StreamPathName, StringComparison.OrdinalIgnoreCase));

            if (path is null)
            {
                missing.Add(reg.StreamPathName);
                _logger.LogWarning(
                    "Expected stream '{StreamPath}' is missing from MediaMTX",
                    reg.StreamPathName);
                continue;
            }

            if (path.SourceReady.HasValue && path.SourceReady == 0)
            {
                notReady.Add(reg.StreamPathName);
                _logger.LogWarning(
                    "Stream '{StreamPath}' exists but source is NOT ready",
                    reg.StreamPathName);
            }

            if (path.Readers is not null && path.Readers.Count == 0)
            {
                noReaders.Add(reg.StreamPathName);
                _logger.LogDebug(
                    "Stream '{StreamPath}' has no active readers",
                    reg.StreamPathName);
            }
        }

        if (missing.Count > 0)
            return OperationResult.Failure(
                $"Missing {missing.Count} streams in MediaMTX: {string.Join(", ", missing)}");

        if (notReady.Count > 0)
            return OperationResult.Failure(
                $"Streams not ready: {string.Join(", ", notReady)}");

        if (noReaders.Count > 0)
        {
            _logger.LogDebug(
                "{Count} streams currently have no readers: {Streams}",
                noReaders.Count, string.Join(", ", noReaders));
        }

        _logger.LogInformation(
            "MediaMTX stream health check passed ({Count} streams verified)",
            expected.Count);

        return OperationResult.Success();
    }

    public async Task<OperationResult> RemoveStreamAsync(
        string deviceId, CancellationToken token = default)
    {
        _logger.LogInformation("Removing stream for camera {DeviceId}", deviceId);

        var reg = _registry.GetByDeviceId(deviceId);
        if (reg is null)
        {
            _logger.LogWarning(
                "RemoveStream skipped for {DeviceId}: not registered", deviceId);
            return OperationResult.Failure($"Camera {deviceId} is not registered");
        }

        if (!_activePaths.ContainsKey(deviceId))
        {
            _logger.LogDebug(
                "RemoveStream skipped for {DeviceId}: not currently active", deviceId);
            return OperationResult.Success();
        }

        var result = await _mtx.DeletePathAsync(reg.StreamPathName, token);
        if (!result.IsSuccess)
        {
            _logger.LogError(
                "MediaMTX DeletePath failed for '{StreamPath}': {Message}",
                reg.StreamPathName, result.Message);
            return result;
        }

        _activePaths.TryRemove(deviceId, out _);
        reg.IsRegisteredWithMtx = false;

        await _registry.UpdateAsync(deviceId, null, reg.MtxConfig);

        _logger.LogInformation(
            "Completed removing stream '{StreamPath}' for camera {DeviceId}",
            reg.StreamPathName, deviceId);

        return OperationResult.Success();
    }

    // ── Config management ─────────────────────────────────────────────────────

    public async Task<OperationResult> UpdateFfmpegOptionsAsync(
        string deviceId, FfmpegCameraOptions ffmpegOptions, CancellationToken token = default)
    {
        var reg = _registry.GetByDeviceId(deviceId);
        if (reg is null)
            return OperationResult.Failure($"Camera {deviceId} is not registered");

        var dbResult = await _registry.UpdateAsync(deviceId, ffmpegOptions, null);
        if (!dbResult.IsSuccess) return dbResult;

        if (_activePaths.ContainsKey(deviceId))
        {
            var newCommand = RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
                ? MediaMtxPathConfig.BuildDshowFfmpegCommand(ffmpegOptions, reg.StreamPathName)
                : MediaMtxPathConfig.BuildV4l2FfmpegCommand(ffmpegOptions, reg.StreamPathName);

            var patch = await _mtx.PatchPathAsync(
                reg.StreamPathName,
                reg.MtxConfig with { RunOnInit = newCommand, RunOnDemand = newCommand },
                token);

            if (!patch.IsSuccess) return patch;
        }

        return OperationResult.Success();
    }

    public async Task<OperationResult> UpdateMtxConfigAsync(
        string deviceId, MediaMtxPathConfig mtxConfig, CancellationToken token = default)
    {
        _logger.LogInformation(
            "Updating MTX config for camera {DeviceId}", deviceId);

        var reg = _registry.GetByDeviceId(deviceId);
        if (reg is null)
            return OperationResult.Failure($"Camera {deviceId} is not registered");

        var dbResult = await _registry.UpdateAsync(deviceId, null, mtxConfig);
        if (!dbResult.IsSuccess)
        {
            _logger.LogError(
                "Failed to persist MTX config for {DeviceId}: {Message}",
                deviceId, dbResult.Message);
            return dbResult;
        }

        if (_activePaths.ContainsKey(deviceId))
        {
            var patch = await _mtx.PatchPathAsync(reg.StreamPathName, mtxConfig, token);
            if (!patch.IsSuccess)
            {
                _logger.LogWarning(
                    "MTX patch failed for '{StreamPath}': {Message}",
                    reg.StreamPathName, patch.Message);
                return patch;
            }
        }

        _logger.LogInformation(
            "Completed updating MTX config for camera {DeviceId}", deviceId);
        return OperationResult.Success();
    }

    // ── Auto-discovery ────────────────────────────────────────────────────────

    public async Task<OperationResultWithValue<int>> AutoDiscoverAsync(
        bool autoAddToMtx, CancellationToken token = default)
    {
        _logger.LogInformation(
            "Starting camera auto-discovery (AutoAddToMtx={AutoAdd})", autoAddToMtx);

        var cameras = await UsbCameraMapper.GetUsbCamerasAsync(token);
        int added = 0;

        foreach (var camera in cameras)
        {
            var existing = _registry.GetByDeviceId(camera.DeviceId);
            if (existing is null)
            {
                _logger.LogDebug(
                    "Auto-discovery found unregistered camera {DeviceId} ({FriendlyName})",
                    camera.DeviceId, camera.FriendlyName);
                continue;
            }

            if (!autoAddToMtx || _activePaths.ContainsKey(camera.DeviceId)) continue;

            var result = await AddStreamAsync(camera.DeviceId, token);
            if (result.IsSuccess) added++;
        }

        _logger.LogInformation(
            "Completed camera auto-discovery: {AddedCount} streams added to MTX", added);

        return OperationResultWithValue<int>.Success(added);
    }

    // ── Hotplug ───────────────────────────────────────────────────────────────

    public async Task<CameraRemapResult> HandleCameraChangedAsync(
        CameraChangedEventArgs e, CancellationToken token = default)
    {
        _logger.LogInformation(
            "Handling camera change {Kind} for {FriendlyName}",
            e.Kind, e.Camera.FriendlyName);

        await _remapLock.WaitAsync(token);
        try
        {
            return e.Kind switch
            {
                CameraChangeKind.Removed => await HandleCameraRemovedAsync(e.Camera, token),
                CameraChangeKind.Added => await HandleCameraAddedAsync(e.Camera, token),
                CameraChangeKind.Updated => await HandleCameraUpdatedAsync(e.Camera, token),
                _ => CameraRemapResult.NoOp()
            };
        }
        finally { _remapLock.Release(); }
    }

    private async Task<CameraRemapResult> HandleCameraRemovedAsync(
        UsbCameraInfo camera, CancellationToken token)
    {
        var reg = _registry.GetByDeviceId(camera.DeviceId);
        if (reg is null) return CameraRemapResult.NoOp();

        await RemoveStreamAsync(camera.DeviceId, token);

        _logger.LogInformation(
            "Camera removed: stream '{StreamPath}' torn down", reg.StreamPathName);

        return new CameraRemapResult(
            DeviceId: camera.DeviceId,
            StreamPathName: reg.StreamPathName,
            Kind: CameraChangeKind.Removed,
            Error: null);
    }

    private async Task<CameraRemapResult> HandleCameraAddedAsync(
        UsbCameraInfo camera, CancellationToken token)
    {
        var reg = _registry.GetByDeviceId(camera.DeviceId);
        if (reg is null) return CameraRemapResult.NoOp();

        // Give the OS a moment to finish device initialisation before FFmpeg
        // tries to open it. This matters more on Linux where udev events fire
        // slightly before the device node is fully ready.
        await Task.Delay(TimeSpan.FromSeconds(3), token);

        var updatedOptions = reg.FfmpegOptions with
        {
            DeviceName = RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
                ? (string.IsNullOrEmpty(camera.SymbolicLink)
                    ? camera.FriendlyName
                    : camera.SymbolicLink)
                : camera.DevicePath   // e.g. /dev/video0 on Linux
        };

        await _registry.UpdateAsync(camera.DeviceId, updatedOptions, null);

        var result = await AddStreamAsync(camera.DeviceId, token);

        _logger.LogInformation(
            "Camera added: stream '{StreamPath}' registered with MTX: {Success}",
            reg.StreamPathName, result.IsSuccess);

        return new CameraRemapResult(
            DeviceId: camera.DeviceId,
            StreamPathName: reg.StreamPathName,
            Kind: CameraChangeKind.Added,
            Error: result.IsSuccess ? null : result.Message);
    }

    private async Task<CameraRemapResult> HandleCameraUpdatedAsync(
        UsbCameraInfo camera, CancellationToken token)
    {
        _logger.LogInformation(
            "Camera updated: refreshing stream for {DeviceId}", camera.DeviceId);

        await HandleCameraRemovedAsync(camera, token);
        var added = await HandleCameraAddedAsync(camera, token);

        return added with { Kind = CameraChangeKind.Updated };
    }

    // ── Command dispatcher ────────────────────────────────────────────────────

    public Task<OperationResult> ExecuteAsync(
        ICameraCommand command, CancellationToken token = default)
        => command.ExecuteAsync(this, token);

    // ── Shutdown ──────────────────────────────────────────────────────────────

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("Stopping camera manager service");

        var removals = _activePaths.Keys
            .Select(id => RemoveStreamAsync(id, cancellationToken));

        await Task.WhenAll(removals);
        _activePaths.Clear();

        await base.StopAsync(cancellationToken);

        _logger.LogInformation(
            "Completed stopping camera manager service: {Success}", true);
    }
}