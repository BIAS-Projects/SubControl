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
    private readonly int _baudRate = 921600;

    private Camera _camera;

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
            [FLIR.LUTtoWHITEHOT] = () => SetLutAndVerify("WHITEHOT", Camera.FLR_COLORLUT_ID_E.FLR_COLORLUT_WHITEHOT),
            [FLIR.LUTtoRAINBOW] = () => SetLutAndVerify("RAINBOW", Camera.FLR_COLORLUT_ID_E.FLR_COLORLUT_RAINBOW),
            [FLIR.LUTtoARCTIC] = () => SetLutAndVerify("ARCTIC", Camera.FLR_COLORLUT_ID_E.FLR_COLORLUT_ARCTIC),
            [FLIR.LUTtoBLACKHOT] = () => SetLutAndVerify("BLACKHOT", Camera.FLR_COLORLUT_ID_E.FLR_COLORLUT_BLACKHOT),
            [FLIR.LUTtoDEFAULT] = () => SetLutAndVerify("DEFAULT", Camera.FLR_COLORLUT_ID_E.FLR_COLORLUT_DEFAULT),
            [FLIR.LUTtoGLOBOW] = () => SetLutAndVerify("GLOBOW", Camera.FLR_COLORLUT_ID_E.FLR_COLORLUT_GLOBOW),
            [FLIR.LUTtoGRADEDFIRE] = () => SetLutAndVerify("GRADEDFIRE", Camera.FLR_COLORLUT_ID_E.FLR_COLORLUT_GRADEDFIRE),
            [FLIR.LUTtoHOTTEST] = () => SetLutAndVerify("HOTTEST", Camera.FLR_COLORLUT_ID_E.FLR_COLORLUT_HOTTEST),
            [FLIR.LUTtoIRONBOW] = () => SetLutAndVerify("IRONBOW", Camera.FLR_COLORLUT_ID_E.FLR_COLORLUT_IRONBOW),
            [FLIR.LUTtoLAVA] = () => SetLutAndVerify("LAVA", Camera.FLR_COLORLUT_ID_E.FLR_COLORLUT_LAVA),
            [FLIR.LUTtoRAINBOW_HC] = () => SetLutAndVerify("RAINBOW_HC", Camera.FLR_COLORLUT_ID_E.FLR_COLORLUT_RAINBOW_HC),

            // NOTE: add a SELFTEST constant to the FLIR command-name class
            // (wherever FLIR.LUTtoWHITEHOT etc. are defined) if you want the
            // TCP command layer to expose this by the same constant pattern.
            // A plain string key works fine in the meantime since dispatch is
            // case-insensitive.
            ["SELFTEST"] = () => RunSelfTestAsync(),
        };
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

            // FIX: Always start with a completely fresh SDK instance to clean internal memory states
            _camera = new Camera();

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

            // Clean up if initialization partially failed
            CleanupCamera();

            return OperationResult.Failure($"Initialize FLIR SDK Error: {ex.Message}");
        }
    }

    public Task StopAsync()
    {
        _logger.LogInformation("Stopping FLIR worker on {Port}", PortPath);

        CleanupCamera();

        _logger.LogInformation("FLIR worker stopped on {Port}", PortPath);

        return Task.CompletedTask;
    }

    /// <summary>
    /// Dispatches a named command to the corresponding FLIR method.
    /// Supported commands (case-insensitive): "whitehot", "rainbow", ...,
    /// and "selftest" (cycles LUTs with read-back verification).
    /// </summary>
    public async Task<OperationResult> WriteTextAsync(string data, CancellationToken token)
    {
        if (!IsOpen)
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

    /// <summary>
    /// Sets a color LUT and then reads it back from the camera to confirm the
    /// change actually took effect.
    ///
    /// This exists because a set call against a stale/half-closed handle (e.g.
    /// a reopen that raced a previous close's native teardown) can return
    /// R_SUCCESS from the SDK without the camera's state actually changing —
    /// which previously surfaced as "command succeeded" while nothing visibly
    /// happened. Reading the LUT back and comparing turns that into an
    /// explicit, loud failure instead of a silent no-op.
    ///
    /// ASSUMPTION: this assumes the Boson SDK exposes a getter with signature
    /// `Camera.FLR_RESULT colorLutGetId(out Camera.FLR_COLORLUT_ID_E id)`.
    /// If your SDK version names/shapes this differently (e.g. it returns the
    /// enum directly rather than via out-param), adjust the call below — the
    /// verification logic around it doesn't need to change.
    /// </summary>
    private async Task<OperationResult> SetLutAndVerify(
        string name,
        Camera.FLR_COLORLUT_ID_E targetLut)
    {
        _logger.LogInformation("Executing FLIR command {Command}", name);

        try
        {
            _camera.isothermSetEnable(Camera.FLR_ENABLE_E.FLR_DISABLE);

            var setResult = _camera.colorLutSetId(targetLut);
            if (setResult != Camera.FLR_RESULT.R_SUCCESS)
            {
                _logger.LogWarning(
                    "FLIR command {Command} failed on set: {Result}", name, setResult);
                return OperationResult.Failure($"{name} error: set failed ({setResult})");
            }

            var readResult = _camera.colorLutGetId(out var actualLut);
            if (readResult != Camera.FLR_RESULT.R_SUCCESS)
            {
                _logger.LogWarning(
                    "FLIR command {Command} set succeeded but read-back failed: {Result}",
                    name, readResult);
                return OperationResult.Failure(
                    $"{name} error: set reported success but read-back failed ({readResult})");
            }

            if (actualLut != targetLut)
            {
                _logger.LogWarning(
                    "FLIR command {Command} mismatch: expected {Expected}, camera reports {Actual}",
                    name, targetLut, actualLut);
                return OperationResult.Failure(
                    $"{name} error: LUT did not change (expected {targetLut}, camera reports " +
                    $"{actualLut}) — port may be in a stale state, try closing and reopening it");
            }

            _logger.LogInformation("Completed FLIR command {Command}: {Success}", name, true);
            return OperationResult.Success();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "FLIR command failed {Command}", name);
            return OperationResult.Failure(ex.Message);
        }
    }

    /// <summary>
    /// Self-test: sets a couple of visually/enum-distinct LUTs in sequence,
    /// verifying each via read-back, then restores DEFAULT. Use this after
    /// opening a port to confirm the camera handle is actually live rather
    /// than trusting that StartAsync() not throwing means writes will work.
    /// </summary>
    private async Task<OperationResult> RunSelfTestAsync()
    {
        _logger.LogInformation("Running FLIR self-test on {Port}", PortPath);

        if (!IsOpen || _camera is null)
            return OperationResult.Failure($"Serial port {PortPath} is closed — cannot self-test");

        var probes = new (string Name, Camera.FLR_COLORLUT_ID_E Lut)[]
        {
            ("WHITEHOT", Camera.FLR_COLORLUT_ID_E.FLR_COLORLUT_WHITEHOT),
            ("IRONBOW",  Camera.FLR_COLORLUT_ID_E.FLR_COLORLUT_IRONBOW),
        };

        foreach (var (name, lut) in probes)
        {
            var result = await SetLutAndVerify($"SELFTEST:{name}", lut);
            if (!result.IsSuccess)
            {
                _logger.LogError(
                    "FLIR self-test FAILED on {Port} at probe {Probe}: {Message}",
                    PortPath, name, result.Message);
                return OperationResult.Failure($"Self-test failed at {name}: {result.Message}");
            }
        }

        // Leave the camera in a known, sane state after probing.
        var restore = await SetLutAndVerify(
            "SELFTEST:restore-DEFAULT", Camera.FLR_COLORLUT_ID_E.FLR_COLORLUT_DEFAULT);

        if (!restore.IsSuccess)
            return OperationResult.Failure(
                $"Self-test probes passed but restore-to-default failed: {restore.Message}");

        _logger.LogInformation("FLIR self-test PASSED on {Port}", PortPath);
        return OperationResult.Success("Self-test passed — LUT set and read-back verified");
    }

    public Task<OperationResult> WriteAsync(byte[] data, CancellationToken token = default)
    {
        throw new NotImplementedException();
    }

    public ValueTask DisposeAsync()
    {
        // FIX: Ensure that if the class itself is disposed, we forcefully release the hardware resources
        CleanupCamera();
        return ValueTask.CompletedTask;
    }

    // Centralized safe cleanup helper
    private void CleanupCamera()
    {
        IsOpen = false;

        if (_camera != null)
        {
            try
            {
                _logger.LogDebug("Closing and releasing FLIR Camera reference.");
                _camera.Close();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Exception caught while closing FLIR camera wrapper.");
            }
            finally
            {
                // Break the reference so GC can collect the underlying native handle allocations
                _camera = null;
            }
        }
    }
}