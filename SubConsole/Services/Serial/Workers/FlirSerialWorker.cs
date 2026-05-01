using Boson;
using Microsoft.Extensions.Logging;
using SubConsole.Models;
using SubConsole.Services;
using SubConsole.Services.Serial;
using System.IO.Ports;
using System.Threading.Channels;

namespace SubConsole.Services.Serial.Workers;

public class FlirSerialWorker : ISerialWorker
{
    public string PortPath { get; set; }
    private readonly int _baudRate  = 921600;

    private Camera _camera = new Camera();

    private readonly Dictionary<string, Func<Task<OperationResult>>> _commands;

    public bool IsOpen { get; private set; }

    public ChannelReader<SerialMessage> ReceivedMessages => throw new NotImplementedException();

    public Task Started => throw new NotImplementedException();

    private readonly ILogger _logger;
    private readonly IDeviceRegistry _registry;

    public FlirSerialWorker(string portName,
        int baudRate,
        ILogger logger,
        IDeviceRegistry registry)
    {
        PortPath = portName;
        _baudRate = baudRate;
        _logger = logger;
        _registry = registry;

        _commands = new Dictionary<string, Func<Task<OperationResult>>>(
            StringComparer.OrdinalIgnoreCase)
        {
            [FLIR.LUTtoWHITEHOT] = FLIRSetLUTtoWHITEHOT,
            [FLIR.LUTtoRAINBOW] = FLIRSetLUTtoRAINBOW,
            [FLIR.LUTtoARCTIC] = FLIRSetLUTtoARCTIC,
            [FLIR.LUTtoBLACKHOT] = FLIRSetLUTtoBLACKHOT,
            [FLIR.LUTtoDEFAULT] = FLIRSetLUTtoDEFAULT,
            [FLIR.LUTtoGLOBOW] = FLIRSetLUTtoGLOBOW,
            [FLIR.LUTtoGRADEDFIRE] = FLIRSetLUTtoGRADEDFIRE,
            [FLIR.LUTtoHOTTEST] = FLIRSetLUTtoHOTTEST,
            [FLIR.LUTtoIRONBOW] = FLIRSetLUTtoIRONBOW,
            [FLIR.LUTtoLAVA] = FLIRSetLUTtoLAVA,
            [FLIR.LUTtoRAINBOW_HC] = FLIRSetLUTtoRAINBOW_HC,

        };
    }

    // ---------------- ISerialWorker ----------------

    public async Task<OperationResult> StartAsync(CancellationToken token)
    {
        try
        {
            _camera.Initialize(PortPath, (uint)_baudRate);
            
            IsOpen = true;
            return OperationResult.Success();
        }
        catch (Exception ex)
        {
            return OperationResult.Failure($"Initialize FLIR SDK Error: {ex.Message}");
        }
    }

    public Task StopAsync()
    {
        _camera.Close();
        IsOpen = false;
        return Task.CompletedTask;
    }

    /// <summary>
    /// Dispatches a named command to the corresponding FLIR method.
    /// Supported commands (case-insensitive): "whitehot", "rainbow"
    /// </summary>
    public async Task<OperationResult> WriteTextAsync(string data, CancellationToken token)
    {
        if(!IsOpen)
        {
            return OperationResult.Failure($"Serial port {PortPath} is closed");
        }
        var command = data?.Trim() ?? string.Empty;

        if (_commands.TryGetValue(command, out var handler))
            return await handler();

        return OperationResult.Failure($"Unknown command: '{command}'. " +
            $"Valid commands: {string.Join(", ", _commands.Keys)}");
    }

    // ---------------- FLIR commands ----------------

    private async Task<OperationResult> FLIRSetLUTtoARCTIC()
    {
        _camera.isothermSetEnable(Camera.FLR_ENABLE_E.FLR_DISABLE);

        var result = _camera.colorLutSetId(Camera.FLR_COLORLUT_ID_E.FLR_COLORLUT_ARCTIC);

        return result == Camera.FLR_RESULT.R_SUCCESS
            ? OperationResult.Success()
            : OperationResult.Failure($"FLIRSetLUTtoARCTIC error: {result}");
    }

    private async Task<OperationResult> FLIRSetLUTtoBLACKHOT()
    {
        _camera.isothermSetEnable(Camera.FLR_ENABLE_E.FLR_DISABLE);

        var result = _camera.colorLutSetId(Camera.FLR_COLORLUT_ID_E.FLR_COLORLUT_BLACKHOT);

        return result == Camera.FLR_RESULT.R_SUCCESS
            ? OperationResult.Success()
            : OperationResult.Failure($"FLIRSetLUTtoBLACKHOT error: {result}");
    }

    private async Task<OperationResult> FLIRSetLUTtoDEFAULT()
    {
        _camera.isothermSetEnable(Camera.FLR_ENABLE_E.FLR_DISABLE);

        var result = _camera.colorLutSetId(Camera.FLR_COLORLUT_ID_E.FLR_COLORLUT_DEFAULT);

        return result == Camera.FLR_RESULT.R_SUCCESS
            ? OperationResult.Success()
            : OperationResult.Failure($"FLIRSetLUTtoDEFAULT error: {result}");
    }


    private async Task<OperationResult> FLIRSetLUTtoGLOBOW()
    {
        _camera.isothermSetEnable(Camera.FLR_ENABLE_E.FLR_DISABLE);

        var result = _camera.colorLutSetId(Camera.FLR_COLORLUT_ID_E.FLR_COLORLUT_GLOBOW);

        return result == Camera.FLR_RESULT.R_SUCCESS
            ? OperationResult.Success()
            : OperationResult.Failure($"FLIRSetLUTtoGLOBOW error: {result}");
    }

    private async Task<OperationResult> FLIRSetLUTtoGRADEDFIRE()
    {
        _camera.isothermSetEnable(Camera.FLR_ENABLE_E.FLR_DISABLE);

        var result = _camera.colorLutSetId(Camera.FLR_COLORLUT_ID_E.FLR_COLORLUT_GRADEDFIRE);

        return result == Camera.FLR_RESULT.R_SUCCESS
            ? OperationResult.Success()
            : OperationResult.Failure($"FLIRSetLUTtoGRADEDFIRE error: {result}");
    }

    private async Task<OperationResult> FLIRSetLUTtoHOTTEST()
    {
        _camera.isothermSetEnable(Camera.FLR_ENABLE_E.FLR_DISABLE);

        var result = _camera.colorLutSetId(Camera.FLR_COLORLUT_ID_E.FLR_COLORLUT_HOTTEST);

        return result == Camera.FLR_RESULT.R_SUCCESS
            ? OperationResult.Success()
            : OperationResult.Failure($"FLIRSetLUTtoHOTTEST error: {result}");
    }

    private async Task<OperationResult> FLIRSetLUTtoIRONBOW()
    {
        _camera.isothermSetEnable(Camera.FLR_ENABLE_E.FLR_DISABLE);

        var result = _camera.colorLutSetId(Camera.FLR_COLORLUT_ID_E.FLR_COLORLUT_IRONBOW);

        return result == Camera.FLR_RESULT.R_SUCCESS
            ? OperationResult.Success()
            : OperationResult.Failure($"FLIRSetLUTtoIRONBOW error: {result}");
    }

    private async Task<OperationResult> FLIRSetLUTtoLAVA()
    {
        _camera.isothermSetEnable(Camera.FLR_ENABLE_E.FLR_DISABLE);

        var result = _camera.colorLutSetId(Camera.FLR_COLORLUT_ID_E.FLR_COLORLUT_LAVA);

        return result == Camera.FLR_RESULT.R_SUCCESS
            ? OperationResult.Success()
            : OperationResult.Failure($"FLIRSetLUTtoLAVA error: {result}");
    }

    private async Task<OperationResult> FLIRSetLUTtoRAINBOW_HC()
    {
        _camera.isothermSetEnable(Camera.FLR_ENABLE_E.FLR_DISABLE);

        var result = _camera.colorLutSetId(Camera.FLR_COLORLUT_ID_E.FLR_COLORLUT_RAINBOW_HC);

        return result == Camera.FLR_RESULT.R_SUCCESS
            ? OperationResult.Success()
            : OperationResult.Failure($"FLIRSetLUTtoRAINBOW_HC error: {result}");
    }



    private async Task<OperationResult> FLIRSetLUTtoWHITEHOT()
    {
        _camera.isothermSetEnable(Camera.FLR_ENABLE_E.FLR_DISABLE);

        var result = _camera.colorLutSetId(Camera.FLR_COLORLUT_ID_E.FLR_COLORLUT_WHITEHOT);

        return result == Camera.FLR_RESULT.R_SUCCESS
            ? OperationResult.Success()
            : OperationResult.Failure($"FLIRSetLUTtoWHITEHOT error: {result}");
    }



    private async Task<OperationResult> FLIRSetLUTtoRAINBOW()
    {
        _camera.isothermSetEnable(Camera.FLR_ENABLE_E.FLR_DISABLE);

        var result = _camera.colorLutSetId(Camera.FLR_COLORLUT_ID_E.FLR_COLORLUT_RAINBOW);

        return result == Camera.FLR_RESULT.R_SUCCESS
            ? OperationResult.Success()
            : OperationResult.Failure($"FLIRSetLUTtoRAINBOW error: {result}");
    }

    public Task<OperationResult> WriteAsync(byte[] data, CancellationToken token = default)
    {
        throw new NotImplementedException();
    }

    public ValueTask DisposeAsync()
    {
        
        return ValueTask.CompletedTask;
    }
}



//using Microsoft.Extensions.Logging;
//using SubConsole.Models;
//using SubConsole.Services.Serial;
//using System.Threading.Channels;

//namespace SubConsole.Services.Serial.Workers;

///// <summary>
///// Serial worker for FLIR Boson cameras.
///// Wraps the Boson SDK (binary, not text).  Received "messages" are
///// synthetic — the SDK call result is packaged as a <see cref="SerialMessage"/>
///// so consumers can use the same channel-based API as text workers.
///// </summary>
//public sealed class FlirSerialWorker : ISerialWorker
//{
//    private readonly ILogger _logger;
//    private readonly IDeviceRegistry _registry;

//    // Boson SDK camera instance — replace type with actual SDK type.
//    // Using object here avoids a hard compile dependency in this file.
//    private object? _camera; // Boson.Camera

//    private CancellationTokenSource _cts = new();

//    private readonly Channel<SerialMessage> _received =
//        Channel.CreateBounded<SerialMessage>(new BoundedChannelOptions(256)
//        { FullMode = BoundedChannelFullMode.DropOldest });

//    public string PortPath { get; }
//    public int BaudRate { get; }
//    public bool IsOpen { get; private set; }
//    public ChannelReader<SerialMessage> ReceivedMessages => _received.Reader;

//    public FlirSerialWorker(
//        string portPath,
//        int baudRate,
//        ILogger logger,
//        IDeviceRegistry registry)
//    {
//        PortPath   = portPath;
//        BaudRate   = baudRate;
//        _logger    = logger;
//        _registry  = registry;
//    }

//    // ── Start / Stop ──────────────────────────────────────────────────────────

//    public Task StartAsync(CancellationToken appToken)
//    {
//        try
//        {
//            // Replace with: _camera = new Boson.Camera();
//            //               ((Boson.Camera)_camera).Initialize(PortPath, (uint)BaudRate);
//            _camera = new object(); // placeholder
//            IsOpen = true;

//            _logger.LogInformation(
//                "FLIR Boson SDK initialised on {Port} @ {Baud}", PortPath, BaudRate);
//        }
//        catch (Exception ex)
//        {
//            _logger.LogError(ex, "Failed to initialise FLIR SDK on {Port}", PortPath);
//            throw;
//        }

//        return Task.CompletedTask;
//    }

//    public Task StopAsync()
//    {
//        _cts.Cancel();
//        IsOpen = false;
//        _received.Writer.TryComplete();
//        _logger.LogInformation("FLIR worker stopped for {Port}", PortPath);
//        return Task.CompletedTask;
//    }

//    // ── Write (SDK calls, not raw bytes) ──────────────────────────────────────

//    /// <summary>
//    /// For FLIR workers, <paramref name="data"/> encodes a command opcode
//    /// in the first byte followed by optional parameters.
//    /// </summary>
//    public async ValueTask<bool> WriteAsync(byte[] data, CancellationToken token = default)
//    {
//        if (!IsOpen || data.Length == 0) return false;

//        try
//        {
//            var result = await DispatchSdkCommandAsync(data[0], data[1..], token);
//            await PublishResultAsync(data[0], result, token);
//            return true;
//        }
//        catch (Exception ex)
//        {
//            _logger.LogError(ex, "FLIR SDK command 0x{Op:X2} failed", data[0]);
//            return false;
//        }
//    }

//    /// <summary>Text writes are not meaningful for binary SDK workers.</summary>
//    public ValueTask<bool> WriteTextAsync(string text, CancellationToken token = default)
//    {
//        _logger.LogWarning(
//            "WriteTextAsync called on FLIR worker — ignoring (use WriteAsync with opcode)");
//        return ValueTask.FromResult(false);
//    }

//    // ── SDK command dispatch ──────────────────────────────────────────────────

//    // Opcodes — extend as needed
//    public const byte OpSetLutWhiteHot = 0x01;
//    public const byte OpSetLutRainbow  = 0x02;
//    public const byte OpSetLutColorful = 0x03;
//    public const byte OpDisableIsotherm = 0x10;
//    public const byte OpEnableIsotherm  = 0x11;

//    private Task<byte[]> DispatchSdkCommandAsync(
//        byte opcode, byte[] parameters, CancellationToken token)
//    {
//        // Replace placeholder returns with actual Boson.Camera calls.
//        // e.g.:
//        //   var cam = (Boson.Camera)_camera!;
//        //   var result = cam.colorLutSetId(Camera.FLR_COLORLUT_ID_E.FLR_COLORLUT_WHITEHOT);
//        //   return Task.FromResult(new[] { (byte)result });

//        return opcode switch
//        {
//            OpSetLutWhiteHot  => Task.FromResult(new byte[] { 0x00 }), // R_SUCCESS
//            OpSetLutRainbow   => Task.FromResult(new byte[] { 0x00 }),
//            OpSetLutColorful  => Task.FromResult(new byte[] { 0x00 }),
//            OpDisableIsotherm => Task.FromResult(new byte[] { 0x00 }),
//            OpEnableIsotherm  => Task.FromResult(new byte[] { 0x00 }),
//            _                 => Task.FromResult(new byte[] { 0xFF })  // unknown
//        };
//    }

//    private async Task PublishResultAsync(byte opcode, byte[] result, CancellationToken token)
//    {
//        var function = _registry.GetFunctionName(PortPath);
//    //    var fn = functions.Count > 0 ? functions[0] : PortPath;

//        var message = new SerialMessage
//        {
//            FunctionName = function,
//            PortPath     = PortPath,
//            Payload      = result,
//            Text         = $"Op=0x{opcode:X2} Result=0x{result[0]:X2}"
//        };

//        await _received.Writer.WriteAsync(message, token);
//    }

//    // ── Disposal ──────────────────────────────────────────────────────────────

//    public async ValueTask DisposeAsync()
//    {
//        await StopAsync();
//        _cts.Dispose();
//    }

//    public ValueTask<bool> WriteFLIRAsync(string text, CancellationToken token = default)
//    {
//        throw new NotImplementedException();
//    }
//}
