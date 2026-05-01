using Microsoft.Extensions.Logging;
using Serilog.Core;
using Serilog.Events;
using SubConsole.Models;
using SubConsole.Services.Serial;
using SubConsole.Services.Serial.Commands;
using System.Text;
using System.Text.Json;
using static SubConsole.Models.UsbDeviceInfo;

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

    private readonly LoggingLevelSwitch _levelSwitch;

    private static readonly JsonSerializerOptions _jsonOptions =
        new() { WriteIndented = false };

    public TcpSerialCommandHandler(
        ISerialCommandDispatcher dispatcher,
        ISerialPortManagerService manager,
        ILogger<TcpSerialCommandHandler> logger,
        LoggingLevelSwitch levelSwitch)
    {
        _dispatcher = dispatcher;
        _manager    = manager;
        _logger     = logger;
        _levelSwitch = levelSwitch;
    }

    // ── Entry point ───────────────────────────────────────────────────────────

    /// <summary>
    /// Parse and execute a TCP command frame.
    /// Returns a JSON ACK/NACK string sent back on the response channel.
    /// Device replies arrive separately via the push channel.
    /// </summary>
    public async Task<string> HandleAsync(TCPMessageBody<string> message, CancellationToken token)
    {
        _logger.LogDebug("Handling frame: {Message}", message);

        try
        {
              return message.Command switch
              {
                  "LIST DEVICES" => await HandleListDevicesAsync(token),
                  "LIST REGISTERED" => await HandleListRegisteredAsync(token),
                  "REGISTER" => await HandleRegisterAsync(message, token),
                  "UNREGISTER" => await HandleUnregisterAsync(message, token),
                  "OPEN" => await HandleOpenAsync(message, token),
                  "CLOSE" => await HandleCloseAsync(message, token),
                  "WRITE" => await HandleWriteAsync(message, token),
                  "WRITE TEXT" => await HandleWriteTextAsync(message, token),
                  //"WRITE FLIR" => await HandleWriteFLIRAsync(message, token),
                  "DISCOVER" => await HandleDiscoverAsync(message, token),
                  "LOGGING" => await HandleLoggingAsync(message, token),
                  "ASSIGN PORT" => HandleAssignPort(message),
                  _ => ErrorResponse($"Unknown command: '{message.Command}'")
              };
        }
        
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error handling frame: {Message}", message);
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
            key          = d.Key,
            vendorId     = d.VendorId,
            productId    = d.ProductId,
            serialNumber = d.SerialNumber,
            manufacturer = d.VendorId,
            description  = d.Description,
            portPath     = d.PortName
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
            functionName = r.FunctionName,
            currentPort   = r.CurrentPortPath
        });

        return SuccessResponse(payload);
    }

    private async Task<string> HandleRegisterAsync(TCPMessageBody<string> message, CancellationToken token)
    {
        int baudrate;
        SerialWorkerType serialWorkerType;
        FunctionToPortEntry entry;
        try
        {
            entry = JsonSerializer.Deserialize<FunctionToPortEntry>(message.Data);
        }
        catch (Exception ex)
        {
            return ErrorResponse($"HandleRegisterAsync JSON DeserializeException: {message.Data} {ex.Message}");
        }
        if(entry is null)
        {
            return ErrorResponse($"HandleRegisterAsync invalid JSON: {message.Data}");
        }
        if(String.IsNullOrWhiteSpace(entry.DeviceKey))
        {
            return ErrorResponse("HandleRegisterAsync DeviceKey cannot be empty");
        }
        if (String.IsNullOrWhiteSpace(entry.FunctionName))
        {
            return ErrorResponse("HandleRegisterAsync FunctionName cannot be empty");
        }
        if (String.IsNullOrWhiteSpace(entry.BaudRate))
        {
            return ErrorResponse("HandleRegisterAsync Baudrate cannot be empty");
        }
        if (!int.TryParse(entry.BaudRate, out baudrate))
        {
            return ErrorResponse("HandleRegisterAsync BaudRate must be numeric");
        }
        if (String.IsNullOrWhiteSpace(entry.WorkerType))
        {
            return ErrorResponse("HandleRegisterAsync WorkerType cannot be empty");
        }
        if (!Enum.TryParse<SerialWorkerType>(entry.WorkerType, out serialWorkerType))
        {
            return ErrorResponse($"HandleRegisterAsync WorkerType must be a registered type: {entry.WorkerType} submitted");
        }

           var devices = await _manager.EnumerateUsbDevicesAsync(token);
        var found = devices.FirstOrDefault(d => d.Key == entry.DeviceKey);

        if (found is null)
            return ErrorResponse($"Device '{entry.DeviceKey}' not found in connected USB Devices");

        var result = await _dispatcher.DispatchAsync(new RegisterDeviceCommand
        {
            Identifier = found,
            FunctionName = entry.FunctionName,
            AutoOpen = false,
            BaudRate = baudrate,
            WorkerType = serialWorkerType
        }, token);

        return result.IsSuccess
            ? SuccessResponse(new { entry.DeviceKey, entry.FunctionName })
            : ErrorResponse(result.Message);
    }




    private async Task<string> HandleUnregisterAsync(TCPMessageBody<string> message, CancellationToken token)
    {

        if (String.IsNullOrWhiteSpace(message.Data))
        {
            return ErrorResponse("HandleUnregisterAsync DeviceKey cannot be empty");
        }

        var result = await _dispatcher.DispatchAsync(
            new UnregisterDeviceCommand { DeviceKey = message.Data }, token);

        return result.IsSuccess
            ? SuccessResponse(new { deviceKey = message.Data })
            : ErrorResponse(result.Message);
    }

    private async Task<string> HandleOpenAsync(TCPMessageBody<string> message, CancellationToken token)
    {
        if (String.IsNullOrWhiteSpace(message.Data))
        {
            return ErrorResponse("HandleUnregisterAsync DeviceKey cannot be empty");
        }



        //if (parts.Length < 2)
        //    return ErrorResponse("OPEN requires: OPEN|deviceKey[|baudRate[|workerType]]");

        //var deviceKey  = parts[1].Trim();
        //var baudRate   = parts.Length >= 3 && int.TryParse(parts[2], out var b) ? b : 115_200;
        //var workerType = parts.Length >= 4
        //    ? Enum.TryParse<SerialWorkerType>(parts[3], true, out var wt) ? wt : SerialWorkerType.Text
        //    : SerialWorkerType.Text;

        var result = await _dispatcher.DispatchAsync(new OpenPortCommand
        {
            DeviceKey  = message.Data,
        }, token);

        return result.IsSuccess
            ? SuccessResponse(new { message.Data })
            : ErrorResponse(result.Message);
    }

    private async Task<string> HandleCloseAsync(TCPMessageBody<string> message, CancellationToken token)
    {

        if (String.IsNullOrWhiteSpace(message.Data))
        {
            return ErrorResponse("HandleUnregisterAsync DeviceKey cannot be empty");
        }

        //if (parts.Length < 2)
        //    return ErrorResponse("CLOSE requires: CLOSE|deviceKey");

        var result = await _dispatcher.DispatchAsync(
            new ClosePortCommand { DeviceKey = message.Data }, token);

        return result.IsSuccess
            ? SuccessResponse(new { deviceKey = message.Data })
            : ErrorResponse(result.Message);
    }

    private async Task<string> HandleWriteAsync(TCPMessageBody<string> message, CancellationToken token)
    {
        if (String.IsNullOrWhiteSpace(message.Command))
        {
            return ErrorResponse("HandleWriteAsync Message Data cannot be empty");
        }
        var result = await _dispatcher.DispatchAsync(
            new WriteCommand { FunctionName = message.Function, Data = Encoding.ASCII.GetBytes(message.Data) }, token);

        return result.IsSuccess
            ? SuccessResponse(new { message.Function, bytesWritten = message.Data.Length })
            : ErrorResponse(result.Message);
    }

    private async Task<string> HandleWriteTextAsync(TCPMessageBody<string> message, CancellationToken token)
    {

        var result = await _dispatcher.DispatchAsync(new WriteTextCommand
        {
            FunctionName  = message.Function,
            Text          = message.Data,
            AppendNewline = true
        }, token);

        return result.IsSuccess
            ? SuccessResponse(new { message.Function })
            : ErrorResponse(result.Message);
    }

    //private async Task<string> HandleWriteFLIRAsync(TCPMessageBody<string> message, CancellationToken token)
    //{

    //    var result = await _dispatcher.DispatchAsync(new WriteFLIRCommand
    //    {
    //        FunctionName = message.Function,
    //        Text = message.Data

    //    }, token);

    //    return result.IsSuccess
    //        ? SuccessResponse(new { message.Function })
    //        : ErrorResponse(result.Message);
    //}


    private async Task<string> HandleDiscoverAsync(TCPMessageBody<string> message, CancellationToken token)
    {
        SerialWorkerType serialWorkerType;
        AutoDiscoverCommand command = JsonSerializer.Deserialize<AutoDiscoverCommand>(message.Command);

        if (command is null)
        {
            return ErrorResponse($"HandleDiscoverAsync invalid JSON: {message.Command}");
        }


        //var autoOpen   = parts.Length < 2 || parts[1].Trim().ToUpperInvariant() != "NOOPEN";
        //        var baudRate   = parts.Length >= 3 && int.TryParse(parts[2], out var b) ? b : 115_200;
        //        var workerType = parts.Length >= 4
        //            ? Enum.TryParse<SerialWorkerType>(parts[3], true, out var wt) ? wt : SerialWorkerType.Text
        //            : SerialWorkerType.Text;

        //var cmd = new AutoDiscoverCommand
        //{
        //    AutoOpenFound     = autoOpe,
        //    DefaultBaudRate   = baudRate,
        //    DefaultWorkerType = workerType
        //};

        await _dispatcher.DispatchAsync(command, token);

        return SuccessResponse(new
        {
            newPortsOpened = command.NewPortsOpened,
            registered     = _manager.GetRegisteredDevices().Count
        });
    }

    private string HandleAssignPort(TCPMessageBody<string> message)
    {
        DeviceKeyToPortPathEntry command = JsonSerializer.Deserialize<DeviceKeyToPortPathEntry>(message.Command);

        if (command is null)
        {
            return ErrorResponse($"HandleAssignPort invalid JSON: {message.Command}");
        }

        // ASSIGN PORT|deviceKey|portPath
        //if (parts.Length < 3)
        //    return ErrorResponse("ASSIGN PORT requires: ASSIGN PORT|deviceKey|portPath");

        //var deviceKey = parts[1].Trim();
        //var portPath  = parts[2].Trim();

        var reg = _manager.GetRegisteredDevices()
            .FirstOrDefault(r => r.Identifier.Key == command.DeviceKey);

        if (reg is null)
            return ErrorResponse($"Device '{command.DeviceKey}' is not registered");

        return SuccessResponse(new { command.DeviceKey, command.PortPath });
    }

    private async Task<string> HandleLoggingAsync(TCPMessageBody<string> message, CancellationToken token)
    {
        switch (message.Data)
        {
            case "Fatal":
                _levelSwitch.MinimumLevel = LogEventLevel.Fatal;
                return SuccessResponse();
                break;
            case "Error": _levelSwitch.MinimumLevel = LogEventLevel.Error;
                return SuccessResponse();
                break;
            case "Warning":
                _levelSwitch.MinimumLevel = LogEventLevel.Warning;
                return SuccessResponse();
                break;
            case "Information":
                _levelSwitch.MinimumLevel = LogEventLevel.Information;
                return SuccessResponse();
                break;
            case "Debug":
                _levelSwitch.MinimumLevel = LogEventLevel.Debug;
                return SuccessResponse();
                break;

            case "Verbose":
                _levelSwitch.MinimumLevel = LogEventLevel.Verbose;
                return SuccessResponse();
                break;
            default:
                return ErrorResponse($"Unknown logging level: {message.Data}");
                break;
        }
    }

 
















    // ── Response helpers ──────────────────────────────────────────────────────

    private static string SuccessResponse(object? data = null)
        => JsonSerializer.Serialize(new { ok = true, data }, _jsonOptions);

    private static string ErrorResponse(string message)
        => JsonSerializer.Serialize(new { ok = false, error = message }, _jsonOptions);
}



//private async Task<string> HandleRegisterAsync(string[] parts, CancellationToken token)
//{
//    // REGISTER|deviceKey|FN1,FN2|baudRate|workerType
//    if (parts.Length < 3)
//        return ErrorResponse("REGISTER requires: REGISTER|deviceKey|FunctionNames[|baudRate[|workerType]]");

//    var deviceKey     = parts[1].Trim();
//    var functionNames = parts[2].Split(',', StringSplitOptions.RemoveEmptyEntries);
//    var baudRate      = parts.Length >= 4 && int.TryParse(parts[3], out var b) ? b : 115_200;
//    var workerType    = parts.Length >= 5
//        ? Enum.TryParse<SerialWorkerType>(parts[4], true, out var wt) ? wt : SerialWorkerType.Text
//        : SerialWorkerType.Text;

//    var devices = await _manager.EnumerateUsbDevicesAsync(token);
//    var found   = devices.FirstOrDefault(d => d.Identifier.Key == deviceKey);

//    if (found.Identifier is null)
//        return ErrorResponse($"Device '{deviceKey}' not found in USB enumeration");

//    var result = await _dispatcher.DispatchAsync(new RegisterDeviceCommand
//    {
//        Identifier    = found.Identifier,
//        FunctionNames = functionNames,
//        AutoOpen      = true,
//        BaudRate      = baudRate,
//        WorkerType    = workerType
//    }, token);

//    return result.IsSuccess
//        ? SuccessResponse(new { deviceKey, functionNames })
//        : ErrorResponse(result.Message);
//}