using Microsoft.Extensions.Logging;
using SubConsole.Models;
using SubConsole.Services.Serial;
using SubConsole.Services.Serial.Commands;
using System.Text.Json;

namespace SubConsole.Services.TCP;

/// <summary>
/// Parses raw TCP frame strings into <see cref="ISerialCommand"/> objects
/// and dispatches them via <see cref="ISerialCommandDispatcher"/>.
///
/// Frame format (client → server):
///   {id}|VERB[|arg1[|arg2[|...]]]
///
/// Examples:
///   abc1|LIST DEVICES
///   abc2|LIST REGISTERED
///   abc3|REGISTER|VID:PID:SN|FUNCTION_A,FUNCTION_B|115200|Text
///   abc4|UNREGISTER|VID:PID:SN
///   abc5|OPEN|VID:PID:SN|115200|Text
///   abc6|CLOSE|VID:PID:SN
///   abc7|WRITE|FUNCTION_NAME|{base64-encoded bytes}
///   abc8|WRITE TEXT|FUNCTION_NAME|the text to send
///   abc9|DISCOVER
///   abcA|ASSIGN PORT|VID:PID:SN|/dev/ttyUSB0
///
/// Server push frames (server → client, no id):
///   PUSH|{FunctionName}|{text or base64}
///
/// Device replies are pushed to the client tagged with the FunctionName of
/// the port they arrived on. The client is responsible for pairing pushes
/// with the commands that triggered them.
/// </summary>
public sealed class TcpSerialCommandHandler
{
    private readonly ISerialCommandDispatcher _dispatcher;
    private readonly ISerialPortManagerService _manager;
    private readonly ILogger<TcpSerialCommandHandler> _logger;

    private static readonly JsonSerializerOptions _jsonOptions =
        new() { WriteIndented = false };

    public TcpSerialCommandHandler(
        ISerialCommandDispatcher dispatcher,
        ISerialPortManagerService manager,
        ILogger<TcpSerialCommandHandler> logger)
    {
        _dispatcher = dispatcher;
        _manager    = manager;
        _logger     = logger;
    }

    // ── Entry point ───────────────────────────────────────────────────────────

    /// <summary>
    /// Parse and execute a TCP command frame.
    /// Returns a JSON ACK/NACK string sent back on the response channel.
    /// Device replies arrive separately via the push channel.
    /// </summary>
    public async Task<string> HandleAsync(string frame, CancellationToken token)
    {
        _logger.LogDebug("Handling frame: {Frame}", frame);

        try
        {
            var parts = frame.Split('|');
            var verb  = parts[0].Trim().ToUpperInvariant();

            return verb switch
            {
                "LIST DEVICES"    => await HandleListDevicesAsync(token),
                "LIST REGISTERED" => await HandleListRegisteredAsync(token),
                "REGISTER"        => await HandleRegisterAsync(parts, token),
                "UNREGISTER"      => await HandleUnregisterAsync(parts, token),
                "OPEN"            => await HandleOpenAsync(parts, token),
                "CLOSE"           => await HandleCloseAsync(parts, token),
                "WRITE"           => await HandleWriteAsync(parts, token),
                "WRITE TEXT"      => await HandleWriteTextAsync(parts, token),
                "DISCOVER"        => await HandleDiscoverAsync(parts, token),
                "ASSIGN PORT"     => HandleAssignPort(parts),
                _                 => ErrorResponse($"Unknown command: '{verb}'")
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error handling frame: {Frame}", frame);
            return ErrorResponse(ex.Message);
        }
    }

    // ── Handlers ──────────────────────────────────────────────────────────────

    public async Task<string> HandleListDevicesAsync(CancellationToken token)
    {
        var cmd = new ListUsbDevicesCommand();
        await _dispatcher.DispatchAsync(cmd, token);

        var payload = cmd.Result!.Select(d => new
        {
            key          = d.Identifier.Key,
            vendorId     = d.Identifier.VendorId,
            productId    = d.Identifier.ProductId,
            serialNumber = d.Identifier.SerialNumber,
            manufacturer = d.Identifier.Manufacturer,
            description  = d.Identifier.Description,
            portPath     = d.PortPath
        });

        return SuccessResponse(payload);
    }

    private async Task<string> HandleListRegisteredAsync(CancellationToken token)
    {
        var cmd = new ListRegisteredDevicesCommand();
        await _dispatcher.DispatchAsync(cmd, token);

        var payload = cmd.Result!.Select(r => new
        {
            key           = r.Identifier.Key,
            description   = r.Identifier.Description,
            functionNames = r.FunctionNames,
            currentPort   = r.CurrentPortPath
        });

        return SuccessResponse(payload);
    }

    private async Task<string> HandleRegisterAsync(string[] parts, CancellationToken token)
    {
        // REGISTER|deviceKey|FN1,FN2|baudRate|workerType
        if (parts.Length < 3)
            return ErrorResponse("REGISTER requires: REGISTER|deviceKey|FunctionNames[|baudRate[|workerType]]");

        var deviceKey     = parts[1].Trim();
        var functionNames = parts[2].Split(',', StringSplitOptions.RemoveEmptyEntries);
        var baudRate      = parts.Length >= 4 && int.TryParse(parts[3], out var b) ? b : 115_200;
        var workerType    = parts.Length >= 5
            ? Enum.TryParse<SerialWorkerType>(parts[4], true, out var wt) ? wt : SerialWorkerType.Text
            : SerialWorkerType.Text;

        var devices = await _manager.EnumerateUsbDevicesAsync(token);
        var found   = devices.FirstOrDefault(d => d.Identifier.Key == deviceKey);

        if (found.Identifier is null)
            return ErrorResponse($"Device '{deviceKey}' not found in USB enumeration");

        var result = await _dispatcher.DispatchAsync(new RegisterDeviceCommand
        {
            Identifier    = found.Identifier,
            FunctionNames = functionNames,
            AutoOpen      = true,
            BaudRate      = baudRate,
            WorkerType    = workerType
        }, token);

        return result.IsSuccess
            ? SuccessResponse(new { deviceKey, functionNames })
            : ErrorResponse(result.Message);
    }

    private async Task<string> HandleUnregisterAsync(string[] parts, CancellationToken token)
    {
        if (parts.Length < 2)
            return ErrorResponse("UNREGISTER requires: UNREGISTER|deviceKey");

        var result = await _dispatcher.DispatchAsync(
            new UnregisterDeviceCommand { DeviceKey = parts[1].Trim() }, token);

        return result.IsSuccess
            ? SuccessResponse(new { deviceKey = parts[1].Trim() })
            : ErrorResponse(result.Message);
    }

    private async Task<string> HandleOpenAsync(string[] parts, CancellationToken token)
    {
        if (parts.Length < 2)
            return ErrorResponse("OPEN requires: OPEN|deviceKey[|baudRate[|workerType]]");

        var deviceKey  = parts[1].Trim();
        var baudRate   = parts.Length >= 3 && int.TryParse(parts[2], out var b) ? b : 115_200;
        var workerType = parts.Length >= 4
            ? Enum.TryParse<SerialWorkerType>(parts[3], true, out var wt) ? wt : SerialWorkerType.Text
            : SerialWorkerType.Text;

        var result = await _dispatcher.DispatchAsync(new OpenPortCommand
        {
            DeviceKey  = deviceKey,
            BaudRate   = baudRate,
            WorkerType = workerType
        }, token);

        return result.IsSuccess
            ? SuccessResponse(new { deviceKey })
            : ErrorResponse(result.Message);
    }

    private async Task<string> HandleCloseAsync(string[] parts, CancellationToken token)
    {
        if (parts.Length < 2)
            return ErrorResponse("CLOSE requires: CLOSE|deviceKey");

        var result = await _dispatcher.DispatchAsync(
            new ClosePortCommand { DeviceKey = parts[1].Trim() }, token);

        return result.IsSuccess
            ? SuccessResponse(new { deviceKey = parts[1].Trim() })
            : ErrorResponse(result.Message);
    }

    private async Task<string> HandleWriteAsync(string[] parts, CancellationToken token)
    {
        // WRITE|functionName|<base64 bytes>
        if (parts.Length < 3)
            return ErrorResponse("WRITE requires: WRITE|functionName|<base64data>");

        var functionName = parts[1].Trim();
        byte[] data;

        try { data = Convert.FromBase64String(parts[2].Trim()); }
        catch { return ErrorResponse("Invalid base64 payload — must be base64-encoded"); }

        var result = await _dispatcher.DispatchAsync(
            new WriteCommand { FunctionName = functionName, Data = data }, token);

        return result.IsSuccess
            ? SuccessResponse(new { functionName, bytesWritten = data.Length })
            : ErrorResponse(result.Message);
    }

    private async Task<string> HandleWriteTextAsync(string[] parts, CancellationToken token)
    {
        // WRITE TEXT|functionName|the text payload
        // Re-join from index 2 onwards so text containing '|' is preserved.
        if (parts.Length < 3)
            return ErrorResponse("WRITE TEXT requires: WRITE TEXT|functionName|text");

        var functionName = parts[1].Trim();
        var text         = string.Join("|", parts[2..]);

        var result = await _dispatcher.DispatchAsync(new WriteTextCommand
        {
            FunctionName  = functionName,
            Text          = text,
            AppendNewline = true
        }, token);

        return result.IsSuccess
            ? SuccessResponse(new { functionName })
            : ErrorResponse(result.Message);
    }

    private async Task<string> HandleDiscoverAsync(string[] parts, CancellationToken token)
    {
        var autoOpen   = parts.Length < 2 || parts[1].Trim().ToUpperInvariant() != "NOOPEN";
        var baudRate   = parts.Length >= 3 && int.TryParse(parts[2], out var b) ? b : 115_200;
        var workerType = parts.Length >= 4
            ? Enum.TryParse<SerialWorkerType>(parts[3], true, out var wt) ? wt : SerialWorkerType.Text
            : SerialWorkerType.Text;

        var cmd = new AutoDiscoverCommand
        {
            AutoOpenFound     = autoOpen,
            DefaultBaudRate   = baudRate,
            DefaultWorkerType = workerType
        };

        await _dispatcher.DispatchAsync(cmd, token);

        return SuccessResponse(new
        {
            newPortsOpened = cmd.NewPortsOpened,
            registered     = _manager.GetRegisteredDevices().Count
        });
    }

    private string HandleAssignPort(string[] parts)
    {
        // ASSIGN PORT|deviceKey|portPath
        if (parts.Length < 3)
            return ErrorResponse("ASSIGN PORT requires: ASSIGN PORT|deviceKey|portPath");

        var deviceKey = parts[1].Trim();
        var portPath  = parts[2].Trim();

        var reg = _manager.GetRegisteredDevices()
            .FirstOrDefault(r => r.Identifier.Key == deviceKey);

        if (reg is null)
            return ErrorResponse($"Device '{deviceKey}' is not registered");

        return SuccessResponse(new { deviceKey, portPath });
    }

    // ── Response helpers ──────────────────────────────────────────────────────

    private static string SuccessResponse(object? data = null)
        => JsonSerializer.Serialize(new { ok = true, data }, _jsonOptions);

    private static string ErrorResponse(string message)
        => JsonSerializer.Serialize(new { ok = false, error = message }, _jsonOptions);
}
