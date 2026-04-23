using Microsoft.Extensions.Logging;
using SubConsole.Models;
using SubConsole.Services.Serial;
using System.IO.Ports;
using System.Text;
using System.Threading.Channels;

namespace SubConsole.Services.Serial.Workers;

// ═════════════════════════════════════════════════════════════════════════════
// Text / line-framed worker (NMEA, AT-command, custom ASCII protocols)
// ═════════════════════════════════════════════════════════════════════════════

public sealed class SerialPortWorker : ISerialWorker
{
    private readonly ILogger _logger;
    private readonly IDeviceRegistry _registry;

    private SerialPort? _port;
    private CancellationTokenSource _cts = new();
    private Task _readerTask = Task.CompletedTask;
    private Task _writerTask = Task.CompletedTask;

    // Inbound to the worker (host → device)
    private readonly Channel<byte[]> _sendQueue =
        Channel.CreateBounded<byte[]>(new BoundedChannelOptions(256)
        { FullMode = BoundedChannelFullMode.DropOldest });

    // Outbound from the worker (device → host)
    private readonly Channel<SerialMessage> _received =
        Channel.CreateBounded<SerialMessage>(new BoundedChannelOptions(1024)
        { FullMode = BoundedChannelFullMode.DropOldest });

    private readonly StringBuilder _lineBuffer = new();

    public string PortPath { get; }
    public bool IsOpen => _port?.IsOpen == true;
    public ChannelReader<SerialMessage> ReceivedMessages => _received.Reader;

    public SerialPortWorker(
        string portPath,
        int baudRate,
        ILogger logger,
        IDeviceRegistry registry)
    {
        PortPath = portPath;
        _logger = logger;
        _registry = registry;

        BaudRate = baudRate;
    }

    public int BaudRate { get; }

    // ── Start / Stop ──────────────────────────────────────────────────────────

    public Task StartAsync(CancellationToken appToken)
    {
        _port = new SerialPort(PortPath, BaudRate)
        {
            WriteTimeout  = 2_000,
            ReadTimeout   = 2_000,
            Encoding      = Encoding.UTF8,
            Handshake     = Handshake.None
        };

        _port.Open();
        _logger.LogInformation("Opened serial port {Port} @ {Baud}", PortPath, BaudRate);

        var linked = CancellationTokenSource
            .CreateLinkedTokenSource(appToken, _cts.Token).Token;

        _readerTask = Task.Run(() => ReadLoopAsync(linked), linked);
        _writerTask = Task.Run(() => WriteLoopAsync(linked), linked);

        return Task.CompletedTask;
    }

    public async Task StopAsync()
    {
        _cts.Cancel();

        if (_port?.IsOpen == true)
            _port.Close();

        await Task.WhenAll(_readerTask, _writerTask)
            .ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);

        _logger.LogInformation("Closed serial port {Port}", PortPath);
    }

    // ── Write ─────────────────────────────────────────────────────────────────

    public ValueTask<bool> WriteAsync(byte[] data, CancellationToken token = default)
        => _sendQueue.Writer.TryWrite(data)
            ? ValueTask.FromResult(true)
            : new ValueTask<bool>(EnqueueAsync(data, token));

    public ValueTask<bool> WriteTextAsync(string text, CancellationToken token = default)
        => WriteAsync(Encoding.UTF8.GetBytes(text), token);

    private async Task<bool> EnqueueAsync(byte[] data, CancellationToken token)
    {
        try
        {
            await _sendQueue.Writer.WriteAsync(data, token).ConfigureAwait(false);
            return true;
        }
        catch (OperationCanceledException)  { return false; }
        catch (ChannelClosedException)      { return false; }
    }

    // ── Read loop (serial → channel) ─────────────────────────────────────────

    private async Task ReadLoopAsync(CancellationToken token)
    {
        var buffer = new byte[4096];

        try
        {
            while (!token.IsCancellationRequested && _port?.IsOpen == true)
            {
                int count = await _port.BaseStream
                    .ReadAsync(buffer, token)
                    .ConfigureAwait(false);

                if (count <= 0) continue;

                var text = Encoding.UTF8.GetString(buffer, 0, count);
                await ProcessIncomingTextAsync(text, token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) { }
        catch (ObjectDisposedException)    { }
        catch (IOException ex)
        {
            _logger.LogError(ex, "Read error on {Port}", PortPath);
        }
        finally
        {
            _received.Writer.TryComplete();
        }
    }

    private async Task ProcessIncomingTextAsync(string chunk, CancellationToken token)
    {
        _lineBuffer.Append(chunk);

        while (true)
        {
            var current = _lineBuffer.ToString();
            var nl = current.IndexOf('\n');
            if (nl < 0) break;

            var line = current[..nl].TrimEnd('\r');
            _lineBuffer.Remove(0, nl + 1);

            var functionName = _registry.GetFunctionName(PortPath);
         //   var primaryFunction = functionNames.Count > 0 ? functionNames[0] : PortPath;

            var message = new SerialMessage
            {
                FunctionName = functionName,
                PortPath     = PortPath,
                Payload      = Encoding.UTF8.GetBytes(line),
                Text         = line
            };

            await _received.Writer.WriteAsync(message, token).ConfigureAwait(false);
        }
    }

    // ── Write loop (channel → serial) ────────────────────────────────────────

    private async Task WriteLoopAsync(CancellationToken token)
    {
        try
        {
            await foreach (var data in _sendQueue.Reader.ReadAllAsync(token))
            {
                if (_port is null || !_port.IsOpen) break;

                try
                {
                    await _port.BaseStream.WriteAsync(data, token).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.LogWarning(ex, "Write error on {Port}", PortPath);
                }
            }
        }
        catch (OperationCanceledException) { }
    }

    // ── Disposal ──────────────────────────────────────────────────────────────

    public async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);
        _cts.Dispose();
        _port?.Dispose();
        _port = null;
    }
}
