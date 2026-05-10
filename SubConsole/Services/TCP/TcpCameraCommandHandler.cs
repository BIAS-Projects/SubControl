using Microsoft.Extensions.Logging;
using Serilog.Core;
using SubConsole.Models;
using SubConsole.Services.Helpers;
using SubConsole.Services.Video;
using System.Text.Json;
using static SQLite.SQLite3;

namespace SubConsole.Services.TCP;

/// <summary>
/// Parses and dispatches TCP commands related to USB cameras and MediaMTX streams.
///
/// Wire protocol — same envelope as serial:
///   Client → Server:  {id}|{json: TCPMessageBody}
///   Server → Client:  {id}|{json result}
///
/// Supported commands (message.Command):
///   LIST CAMERAS          — enumerate connected USB cameras
///   LIST REGISTERED       — list camera registrations
///   REGISTER              — register a camera + MTX config
///   UNREGISTER            — remove a camera registration
///   ADD STREAM            — push path config to MediaMTX
///   REMOVE STREAM         — delete path from MediaMTX
///   UPDATE FFMPEG         — update FFmpeg options for a camera
///   UPDATE MTX            — update MediaMTX path config
///   DISCOVER              — auto-discover + optionally add streams
/// </summary>
public sealed class TcpCameraCommandHandler
{
    private readonly ICameraManagerService _manager;
    private readonly ILogger<TcpCameraCommandHandler> _logger;

    private static readonly JsonSerializerOptions _jsonOptions =
        new() { WriteIndented = false };

    public TcpCameraCommandHandler(
        ICameraManagerService manager,
        ILogger<TcpCameraCommandHandler> logger)
    {
        _manager = manager;
        _logger = logger;
    }

    // ── Entry point ───────────────────────────────────────────────────────────

    public async Task<string> HandleAsync(
      TCPMessageBody<string> message, CancellationToken token)
    {
        _logger.LogDebug(
            "Handling camera command {Command}", message.Command);
        try
        {
            string data = message.Command switch
            {
                "LIST CAMERAS" => await HandleListCamerasAsync(token),
                "LIST REGISTERED" => await HandleListRegisteredAsync(token),
                "REGISTER" => await HandleRegisterAsync(message, token),
                "UNREGISTER" => await HandleUnregisterAsync(message, token),
                "ADD STREAM" => await HandleAddStreamAsync(message, token),
                "REMOVE STREAM" => await HandleRemoveStreamAsync(message, token),
                "UPDATE FFMPEG" => await HandleUpdateFfmpegAsync(message, token),
                "UPDATE MTX" => await HandleUpdateMtxAsync(message, token),
                "DISCOVER" => await HandleDiscoverAsync(message, token),
                "CHECK FFMPEG" => await HandleCheckFfmpegAsync(token),
                "CHECK MTX VERSION" => await HandleCheckMtxVersionAsync(token),
                "CHECK MTX STREAMS" => await HandleCheckMtxStreamsAsync(token),
                _ => await UnknownCommand(message.Command)
            };

            var response = new TCPMessageBody<string>(
                Function: message.Function,
                Command: message.Command,
                Data: data);

            return JsonSerializer.Serialize(response);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "Camera command {Command} threw unexpectedly", message.Command);

            var errorResponse = new TCPMessageBody<string>(
                Function: message.Function,
                Command: message.Command,
                Data: ErrorResponse(ex.Message));

            return JsonSerializer.Serialize(errorResponse);
        }
    }

    // ── Handlers ──────────────────────────────────────────────────────────────


    private async Task<string> UnknownCommand(string command)
    {
        _logger.LogWarning(
            "Unknown Camera Command: {Command}",
            command);
        return ErrorResponse("Unknown Camera Command: {command}");
    }

    private async Task<string> HandleCheckFfmpegAsync(CancellationToken token)
    {
        _logger.LogInformation("Checking FFmpeg availability");

        var result = await _manager.CheckFfmpegAsync(token);

        if (!result.IsSuccess)
        {
            _logger.LogError(
                "FFmpeg check failed: {Message}",
                result.Message);

            return ErrorResponse(result.Message);
        }

        _logger.LogInformation(
            "FFmpeg is available: {Version}",
            result.Value);

        return SuccessResponse(new
        {
            version = result.Value
        });
    }

    private async Task<string> HandleCheckMtxVersionAsync(CancellationToken token)
    {
        _logger.LogInformation("Checking MediaMTX version");

        var result = await _manager.GetMediaMtxVersionAsync(token);

        if (!result.IsSuccess)
        {
            _logger.LogError(
                "MediaMTX version check failed: {Message}",
                result.Message);

            return ErrorResponse(result.Message);
        }

        _logger.LogInformation(
            "MediaMTX version: {Version}",
            result.Value);

        return SuccessResponse(new
        {
            version = result.Value
        });
    }


    private async Task<string> HandleCheckMtxStreamsAsync(CancellationToken token)
    {
        _logger.LogInformation("Checking MediaMTX stream health");

        var result = await _manager.CheckMediaMtxStreamsAsync(token);

        if (!result.IsSuccess)
        {
            _logger.LogError(
                "MediaMTX stream health check failed: {Message}",
                result.Message);

            return ErrorResponse(result.Message);
        }

        var registeredCount = _manager.GetRegisteredCameras().Count;

        _logger.LogInformation(
            "MediaMTX stream health check passed ({Count} registered cameras)",
            registeredCount);

        return SuccessResponse(new
        {
            registered = registeredCount
        });
    }




    private async Task<string> HandleListCamerasAsync(CancellationToken token)
    {
        _logger.LogDebug("Executing LIST CAMERAS");

        var cameras = await _manager.EnumerateCamerasAsync(token);

        var payload = cameras.Select(c => new
        {
            deviceId = c.DeviceId,
            friendlyName = c.FriendlyName,
            vendorId = c.VendorId,
            productId = c.ProductId,
            serialNumber = c.SerialNumber,
            devicePath = c.DevicePath,
            symbolicLink = c.SymbolicLink
        });

        _logger.LogInformation(
            "LIST CAMERAS returned {Count} cameras", cameras.Count);

        return SuccessResponse(payload);
    }

    private Task<string> HandleListRegisteredAsync(CancellationToken token)
    {
        _logger.LogDebug("Executing camera LIST REGISTERED");

        var registrations = _manager.GetRegisteredCameras();

        var payload = registrations.Select(r => new
        {
            deviceId = r.Camera.DeviceId,
            friendlyName = r.Camera.FriendlyName,
            streamPathName = r.StreamPathName,
            isRegisteredWithMtx = r.IsRegisteredWithMtx,
            ffmpegOptions = new
            {
                deviceName = r.FfmpegOptions.DeviceName,
                width = r.FfmpegOptions.Width,
                height = r.FfmpegOptions.Height,
                framerate = r.FfmpegOptions.Framerate,
                pixelFormat = r.FfmpegOptions.PixelFormat,
                videoCodec = r.FfmpegOptions.VideoCodec,
                preset = r.FfmpegOptions.Preset,
                tune = r.FfmpegOptions.Tune,
                bitrate = r.FfmpegOptions.Bitrate
            }
        });

        _logger.LogInformation(
            "LIST REGISTERED (cameras) returned {Count} entries", registrations.Count);

        return Task.FromResult(SuccessResponse(payload));
    }

    private async Task<string> HandleRegisterAsync(
        TCPMessageBody<string> message, CancellationToken token)
    {
        _logger.LogDebug("Processing camera REGISTER command");

        CameraRegistrationRequest? request;
        try
        {
            request = JsonSerializer.Deserialize<CameraRegistrationRequest>(message.Data);
        }
        catch (Exception ex)
        {
            return ErrorResponse(
                $"REGISTER deserialisation failed: {ex.Message}");
        }

        if (request is null)
            return ErrorResponse("REGISTER: payload was null");
        if (string.IsNullOrWhiteSpace(request.DeviceId))
            return ErrorResponse("REGISTER: DeviceId cannot be empty");
        if (string.IsNullOrWhiteSpace(request.StreamPathName))
            return ErrorResponse("REGISTER: StreamPathName cannot be empty");
        if (string.IsNullOrWhiteSpace(request.FfmpegOptions?.DeviceName))
            return ErrorResponse("REGISTER: FfmpegOptions.DeviceName cannot be empty");

        // Soft-check: warn if not currently present but allow registration anyway.
        // The camera will be matched on plug-in via UsbCameraRegistry.CameraChanged.
        var cameras = await _manager.EnumerateCamerasAsync(token);
        var found = cameras.FirstOrDefault(c => c.DeviceId == request.DeviceId);
        if (found is null)
        {
            _logger.LogWarning(
                "REGISTER: camera {DeviceId} not currently connected — " +
                "registering anyway, stream will be added when device is detected",
                request.DeviceId);

            // Build a minimal UsbCameraInfo from the request so registration
            // can proceed without the physical device being present
            found = new UsbCameraInfo { DeviceId = request.DeviceId };
        }

        var result = await _manager.RegisterCameraAsync(
            found,
            request.StreamPathName,
            request.FfmpegOptions,
            request.MtxConfig ?? new MediaMtxPathConfig
            {
                RunOnDemandRestart = true,
                RunOnDemandStartTimeout = "10s",
                RunOnDemandCloseAfter = "10s"
            },
            token);

        if (result.IsSuccess)
        {
            _logger.LogInformation(
                "Camera {DeviceId} registered as '{StreamPath}'",
                request.DeviceId, request.StreamPathName);
        }
        else
        {
            _logger.LogWarning(
                "REGISTER failed for {DeviceId}: {Reason}",
                request.DeviceId, result.Message);
        }

        return result.IsSuccess
            ? SuccessResponse(new
            {
                deviceId = request.DeviceId,
                streamPathName = request.StreamPathName
            })
            : ErrorResponse(result.Message);
    }

    private async Task<string> HandleUnregisterAsync(
        TCPMessageBody<string> message, CancellationToken token)
    {
        if (string.IsNullOrWhiteSpace(message.Data))
            return ErrorResponse("UNREGISTER: DeviceId cannot be empty");

        _logger.LogInformation(
            "Unregistering camera {DeviceId}", message.Data);

        var result = await _manager.UnregisterCameraAsync(message.Data, token);

        return result.IsSuccess
            ? SuccessResponse(new { deviceId = message.Data })
            : ErrorResponse(result.Message);
    }

    private async Task<string> HandleAddStreamAsync(
        TCPMessageBody<string> message, CancellationToken token)
    {
        if (string.IsNullOrWhiteSpace(message.Data))
            return ErrorResponse("ADD STREAM: DeviceId cannot be empty");

        _logger.LogInformation(
            "Adding stream for camera {DeviceId}", message.Data);

        var result = await _manager.AddStreamAsync(message.Data, token);

        return result.IsSuccess
            ? SuccessResponse(new { deviceId = message.Data })
            : ErrorResponse(result.Message);
    }

    private async Task<string> HandleRemoveStreamAsync(
        TCPMessageBody<string> message, CancellationToken token)
    {
        if (string.IsNullOrWhiteSpace(message.Data))
            return ErrorResponse("REMOVE STREAM: DeviceId cannot be empty");

        _logger.LogInformation(
            "Removing stream for camera {DeviceId}", message.Data);

        var result = await _manager.RemoveStreamAsync(message.Data, token);

        return result.IsSuccess
            ? SuccessResponse(new { deviceId = message.Data })
            : ErrorResponse(result.Message);
    }

    private async Task<string> HandleUpdateFfmpegAsync(
        TCPMessageBody<string> message, CancellationToken token)
    {
        _logger.LogDebug("Processing UPDATE FFMPEG command");

        UpdateFfmpegRequest? request;
        try
        {
            request = JsonSerializer.Deserialize<UpdateFfmpegRequest>(message.Data);
        }
        catch (Exception ex)
        {
            return ErrorResponse($"UPDATE FFMPEG deserialisation failed: {ex.Message}");
        }

        if (request is null || string.IsNullOrWhiteSpace(request.DeviceId))
            return ErrorResponse("UPDATE FFMPEG: DeviceId cannot be empty");

        var result = await _manager.UpdateFfmpegOptionsAsync(
            request.DeviceId, request.FfmpegOptions, token);

        return result.IsSuccess
            ? SuccessResponse(new { request.DeviceId })
            : ErrorResponse(result.Message);
    }

    private async Task<string> HandleUpdateMtxAsync(
        TCPMessageBody<string> message, CancellationToken token)
    {
        _logger.LogDebug("Processing UPDATE MTX command");

        UpdateMtxRequest? request;
        try
        {
            request = JsonSerializer.Deserialize<UpdateMtxRequest>(message.Data);
        }
        catch (Exception ex)
        {
            return ErrorResponse($"UPDATE MTX deserialisation failed: {ex.Message}");
        }

        if (request is null || string.IsNullOrWhiteSpace(request.DeviceId))
            return ErrorResponse("UPDATE MTX: DeviceId cannot be empty");

        var result = await _manager.UpdateMtxConfigAsync(
            request.DeviceId, request.MtxConfig, token);

        return result.IsSuccess
            ? SuccessResponse(new { request.DeviceId })
            : ErrorResponse(result.Message);
    }

    private async Task<string> HandleDiscoverAsync(
        TCPMessageBody<string> message, CancellationToken token)
    {
        _logger.LogInformation("Starting camera auto-discovery");

        bool autoAdd = true;

        // Optional: client can pass { "autoAdd": false } to scan without streaming
        if (!string.IsNullOrWhiteSpace(message.Data))
        {
            try
            {
                var opts = JsonSerializer.Deserialize<CameraDiscoverRequest>(message.Data);
                if (opts is not null) autoAdd = opts.AutoAdd;
            }
            catch
            {
                // Malformed JSON — default to autoAdd = true, non-fatal
                _logger.LogWarning(
                    "DISCOVER: could not parse options, using defaults");
            }
        }

        var result = await _manager.AutoDiscoverAsync(autoAdd, token);

        return result.IsSuccess
            ? SuccessResponse(new
            {
                streamsAdded = result.Value,
                registered = _manager.GetRegisteredCameras().Count
            })
            : ErrorResponse(result.Message);
    }

    // ── Response helpers ──────────────────────────────────────────────────────

    private static string SuccessResponse(object? data = null)
        => JsonSerializer.Serialize(new { ok = true, data }, _jsonOptions);

    private static string ErrorResponse(string message)
        => JsonSerializer.Serialize(new { ok = false, error = message }, _jsonOptions);
}

// ── Request DTOs ──────────────────────────────────────────────────────────────

/// <summary>Payload for the REGISTER camera command.</summary>
public sealed class CameraRegistrationRequest
{
    public string DeviceId { get; init; } = "";
    public string StreamPathName { get; init; } = "";
    public FfmpegCameraOptions FfmpegOptions { get; init; } = new();
    public MediaMtxPathConfig? MtxConfig { get; init; }
}

public sealed class UpdateFfmpegRequest
{
    public string DeviceId { get; init; } = "";
    public FfmpegCameraOptions FfmpegOptions { get; init; } = new();
}

public sealed class UpdateMtxRequest
{
    public string DeviceId { get; init; } = "";
    public MediaMtxPathConfig MtxConfig { get; init; } = new();
}

public sealed class CameraDiscoverRequest
{
    public bool AutoAdd { get; init; } = true;
}