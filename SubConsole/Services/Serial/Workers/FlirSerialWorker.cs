using Boson;
using Microsoft.Extensions.Logging;
using SubConsole.Models;
using SubConsole.Services;
using SubConsole.Services.Serial;
using System.IO.Ports;
using System.Threading.Channels;
using static SQLite.SQLite3;

namespace SubConsole.Services.Serial.Workers;

public class FlirSerialWorker : ISerialWorker
{
    public string PortPath { get; set; }
    private readonly int _baudRate  = 921600;

    private Camera _camera = new Camera();

    private readonly Dictionary<string, Func<Task<OperationResult>>> _commands;

    public bool IsOpen { get; private set; }

    private readonly Channel<SerialMessage> _received =
        Channel.CreateBounded<SerialMessage>(new BoundedChannelOptions(256)
        { FullMode = BoundedChannelFullMode.DropOldest });

    public ChannelReader<SerialMessage> ReceivedMessages => _received.Reader;
    public Task Started => Task.CompletedTask;


    private readonly ILogger<FlirSerialWorker> _logger;
    private readonly IDeviceRegistry _registry;

    public FlirSerialWorker(string portName,
        int baudRate,
        ILogger<FlirSerialWorker> logger,
        IDeviceRegistry registry)
    {
        PortPath = portName;
        _baudRate = baudRate;
        _logger = logger;
        _registry = registry;

        _commands = new Dictionary<string, Func<Task<OperationResult>>>(
            StringComparer.OrdinalIgnoreCase)
        {
            [FLIR.LUTtoWHITEHOT] = () => ExecuteFlirCommand("WHITEHOT", FLIRSetLUTtoWHITEHOT),
            [FLIR.LUTtoRAINBOW] = () => ExecuteFlirCommand("RAINBOW", FLIRSetLUTtoRAINBOW),
            [FLIR.LUTtoARCTIC] = () => ExecuteFlirCommand("ARCTIC", FLIRSetLUTtoARCTIC),
            [FLIR.LUTtoBLACKHOT] = () => ExecuteFlirCommand("BLACKHOT", FLIRSetLUTtoBLACKHOT),
            [FLIR.LUTtoDEFAULT] = () => ExecuteFlirCommand("DEFAULT", FLIRSetLUTtoDEFAULT),
            [FLIR.LUTtoGLOBOW] = () => ExecuteFlirCommand("GLOBOW", FLIRSetLUTtoGLOBOW),
            [FLIR.LUTtoGRADEDFIRE] = () => ExecuteFlirCommand("GRADEDFIRE", FLIRSetLUTtoGRADEDFIRE),
            [FLIR.LUTtoHOTTEST] = () => ExecuteFlirCommand("HOTTEST", FLIRSetLUTtoHOTTEST),
            [FLIR.LUTtoIRONBOW] = () => ExecuteFlirCommand("IRONBOW", FLIRSetLUTtoIRONBOW),
            [FLIR.LUTtoLAVA] = () => ExecuteFlirCommand("LAVA", FLIRSetLUTtoLAVA),
            [FLIR.LUTtoRAINBOW_HC] = () => ExecuteFlirCommand("RAINBOW_HC", FLIRSetLUTtoRAINBOW_HC),

        };
    }


    private async Task<OperationResult> ExecuteFlirCommand(
    string name,
    Func<Task<OperationResult>> action)
    {
        _logger.LogInformation("Executing FLIR command {Command}", name);

        try
        {
            var result = await action();

            _logger.LogInformation(
                "Completed FLIR command {Command}: {Success}",
                name,
                result.IsSuccess);

            return result;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "FLIR command failed {Command}", name);
            return OperationResult.Failure(ex.Message);
        }
    }

    // ---------------- ISerialWorker ----------------

    public async Task<OperationResult> StartAsync(CancellationToken token)
    {
        try
        {

            _logger.LogInformation(
            "Starting FLIR worker on {Port} @ {Baud}",
                PortPath,
                _baudRate);

            _camera.Initialize(PortPath, (uint)_baudRate);
            
            IsOpen = true;

            _logger.LogInformation(
                "FLIR camera initialized successfully on {Port}",
                PortPath);

            return OperationResult.Success();
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "Failed to initialize FLIR camera on {Port}",
                PortPath);
            return OperationResult.Failure($"Initialize FLIR SDK Error: {ex.Message}");
        }
    }

    public Task StopAsync()
    {
        _logger.LogInformation("Stopping FLIR worker on {Port}", PortPath);

        _camera.Close();
        IsOpen = false;

        _logger.LogInformation("FLIR worker stopped on {Port}", PortPath);

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
            _logger.LogWarning(
                "Rejected command because FLIR worker is closed. Port={Port}",
                PortPath);
            return OperationResult.Failure($"Serial port {PortPath} is closed");
        }
        var command = data?.Trim() ?? string.Empty;

        _logger.LogInformation(
            "FLIR command received: '{Command}' on {Port}",
            command,
            PortPath);

        if (_commands.TryGetValue(command, out var handler))
        {
            _logger.LogDebug(
                "Dispatching FLIR command '{Command}'",
                command);

            var result = await handler();

            _logger.LogInformation(
                "FLIR command '{Command}' completed: {Success}",
                command,
                result.IsSuccess);

            return result;



        }

        _logger.LogWarning(
            "Unknown FLIR command '{Command}' on {Port}. Known commands: {Known}",
            command,
            PortPath,
            string.Join(", ", _commands.Keys));

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




