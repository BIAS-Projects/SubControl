using Microsoft.Extensions.Logging;
using SubConsole.Models;
using SubConsole.Services.Serial;
using System.Threading.Channels;

namespace SubConsole.Services.Serial.Workers;

/// <summary>
/// Serial worker for FLIR Boson cameras.
/// Wraps the Boson SDK (binary, not text).  Received "messages" are
/// synthetic — the SDK call result is packaged as a <see cref="SerialMessage"/>
/// so consumers can use the same channel-based API as text workers.
/// </summary>
public sealed class FlirSerialWorker : ISerialWorker
{
    private readonly ILogger _logger;
    private readonly IDeviceRegistry _registry;

    // Boson SDK camera instance — replace type with actual SDK type.
    // Using object here avoids a hard compile dependency in this file.
    private object? _camera; // Boson.Camera

    private CancellationTokenSource _cts = new();

    private readonly Channel<SerialMessage> _received =
        Channel.CreateBounded<SerialMessage>(new BoundedChannelOptions(256)
        { FullMode = BoundedChannelFullMode.DropOldest });

    public string PortPath { get; }
    public int BaudRate { get; }
    public bool IsOpen { get; private set; }
    public ChannelReader<SerialMessage> ReceivedMessages => _received.Reader;

    public FlirSerialWorker(
        string portPath,
        int baudRate,
        ILogger logger,
        IDeviceRegistry registry)
    {
        PortPath   = portPath;
        BaudRate   = baudRate;
        _logger    = logger;
        _registry  = registry;
    }

    // ── Start / Stop ──────────────────────────────────────────────────────────

    public Task StartAsync(CancellationToken appToken)
    {
        try
        {
            // Replace with: _camera = new Boson.Camera();
            //               ((Boson.Camera)_camera).Initialize(PortPath, (uint)BaudRate);
            _camera = new object(); // placeholder
            IsOpen = true;

            _logger.LogInformation(
                "FLIR Boson SDK initialised on {Port} @ {Baud}", PortPath, BaudRate);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to initialise FLIR SDK on {Port}", PortPath);
            throw;
        }

        return Task.CompletedTask;
    }

    public Task StopAsync()
    {
        _cts.Cancel();
        IsOpen = false;
        _received.Writer.TryComplete();
        _logger.LogInformation("FLIR worker stopped for {Port}", PortPath);
        return Task.CompletedTask;
    }

    // ── Write (SDK calls, not raw bytes) ──────────────────────────────────────

    /// <summary>
    /// For FLIR workers, <paramref name="data"/> encodes a command opcode
    /// in the first byte followed by optional parameters.
    /// </summary>
    public async ValueTask<bool> WriteAsync(byte[] data, CancellationToken token = default)
    {
        if (!IsOpen || data.Length == 0) return false;

        try
        {
            var result = await DispatchSdkCommandAsync(data[0], data[1..], token);
            await PublishResultAsync(data[0], result, token);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "FLIR SDK command 0x{Op:X2} failed", data[0]);
            return false;
        }
    }

    /// <summary>Text writes are not meaningful for binary SDK workers.</summary>
    public ValueTask<bool> WriteTextAsync(string text, CancellationToken token = default)
    {
        _logger.LogWarning(
            "WriteTextAsync called on FLIR worker — ignoring (use WriteAsync with opcode)");
        return ValueTask.FromResult(false);
    }

    // ── SDK command dispatch ──────────────────────────────────────────────────

    // Opcodes — extend as needed
    public const byte OpSetLutWhiteHot = 0x01;
    public const byte OpSetLutRainbow  = 0x02;
    public const byte OpSetLutColorful = 0x03;
    public const byte OpDisableIsotherm = 0x10;
    public const byte OpEnableIsotherm  = 0x11;

    private Task<byte[]> DispatchSdkCommandAsync(
        byte opcode, byte[] parameters, CancellationToken token)
    {
        // Replace placeholder returns with actual Boson.Camera calls.
        // e.g.:
        //   var cam = (Boson.Camera)_camera!;
        //   var result = cam.colorLutSetId(Camera.FLR_COLORLUT_ID_E.FLR_COLORLUT_WHITEHOT);
        //   return Task.FromResult(new[] { (byte)result });

        return opcode switch
        {
            OpSetLutWhiteHot  => Task.FromResult(new byte[] { 0x00 }), // R_SUCCESS
            OpSetLutRainbow   => Task.FromResult(new byte[] { 0x00 }),
            OpSetLutColorful  => Task.FromResult(new byte[] { 0x00 }),
            OpDisableIsotherm => Task.FromResult(new byte[] { 0x00 }),
            OpEnableIsotherm  => Task.FromResult(new byte[] { 0x00 }),
            _                 => Task.FromResult(new byte[] { 0xFF })  // unknown
        };
    }

    private async Task PublishResultAsync(byte opcode, byte[] result, CancellationToken token)
    {
        var functions = _registry.GetFunctionNames(PortPath);
        var fn = functions.Count > 0 ? functions[0] : PortPath;

        var message = new SerialMessage
        {
            FunctionName = fn,
            PortPath     = PortPath,
            Payload      = result,
            Text         = $"Op=0x{opcode:X2} Result=0x{result[0]:X2}"
        };

        await _received.Writer.WriteAsync(message, token);
    }

    // ── Disposal ──────────────────────────────────────────────────────────────

    public async ValueTask DisposeAsync()
    {
        await StopAsync();
        _cts.Dispose();
    }
}
