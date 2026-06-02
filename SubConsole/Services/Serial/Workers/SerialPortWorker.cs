using Microsoft.Extensions.Logging;
using SubConsole.Models;
using SubConsole.Services.Serial;
using System.IO.Ports;
using System.Text;
using System.Threading.Channels;
using System.Xml.Linq;

namespace SubConsole.Services.Serial.Workers;

// ═════════════════════════════════════════════════════════════════════════════
// Text / line-framed worker (NMEA, AT-command, custom ASCII protocols)
// ═════════════════════════════════════════════════════════════════════════════

public sealed class SerialPortWorker : ISerialWorker
{
    // Signals the fan-in loop the moment this worker is ready to produce messages
    private readonly TaskCompletionSource _startedTcs =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    public Task Started => _startedTcs.Task;

    private readonly ILogger<SerialPortWorker> _logger;
    private readonly IDeviceRegistry _registry;

    private int _stopped = 0; // 0 = running, 1 = stopped
    private string _functionName;

    private SerialPort? _port;
    private CancellationTokenSource _cts = new();
    private Task _readerTask = Task.CompletedTask;
    private Task _writerTask = Task.CompletedTask;

    // Inbound to the worker (host → device)
    private readonly Channel<byte[]> _sendQueue =
        Channel.CreateBounded<byte[]>(new BoundedChannelOptions(256)
        { FullMode = BoundedChannelFullMode.Wait });

    // Outbound from the worker (device → host)
    private readonly Channel<SerialMessage> _received =
        Channel.CreateBounded<SerialMessage>(new BoundedChannelOptions(1024)
        { FullMode = BoundedChannelFullMode.Wait });

    private readonly StringBuilder _lineBuffer = new();

    public string PortPath { get; }
    public bool IsOpen => _port?.IsOpen == true;
    public ChannelReader<SerialMessage> ReceivedMessages => _received.Reader;

    public SerialPortWorker(
        string portPath,
        int baudRate,
        ILogger<SerialPortWorker> logger,
        IDeviceRegistry registry)
    {
        PortPath = portPath;
        _logger = logger;
        _registry = registry;

        BaudRate = baudRate;
    }

    public int BaudRate { get; }

    // ── Start / Stop ──────────────────────────────────────────────────────────
    public async Task<OperationResult> StartAsync(CancellationToken appToken)
    {
        try
        {
            StopBits _stopBits = StopBits.One;



            _functionName = _registry.GetFunctionName(PortPath) ?? PortPath; // ← add this

            if (_functionName.Contains("TOM"))
            {
                _stopBits = StopBits.Two;
            }

            _port = new SerialPort(PortPath, BaudRate) {
                WriteTimeout = 2_000,
                ReadTimeout = 2_000,
                //Encoding = Encoding.UTF8,
                Encoding = Encoding.ASCII,
                Handshake = Handshake.None,
                StopBits = _stopBits
            };
            _port.Open();
            _logger.LogInformation(
                "Starting serial worker for {Function} on {Port} @ {Baud}",
                _functionName,
                PortPath,
                BaudRate);

            var linked = CancellationTokenSource
                .CreateLinkedTokenSource(appToken, _cts.Token).Token;

            _readerTask = Task.Run(() => ReadLoopAsync(linked), linked);
            _writerTask = Task.Run(() => WriteLoopAsync(linked), linked);

            _startedTcs.TrySetResult();
            _logger.LogInformation(
                "Started serial worker for {Function} on {Port} ({Function})",
                _functionName,
                PortPath,
                _functionName);
            return OperationResult.Success();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "Failed to start serial worker for {Function} on {Port}",
                _functionName,
                PortPath);
            _startedTcs.TrySetException(ex);
            return OperationResult.Failure($"Error starting serial worker: {ex.Message}");
        }
    }

    public async Task StopAsync()
    {
        _logger.LogInformation(
            "Stopping serial worker for {Function} on {Port}",
            _functionName,
            PortPath);
        if (Interlocked.Exchange(ref _stopped, 1) == 1) return; // already stopped

        _cts.Cancel();
        if (_port?.IsOpen == true)
            _port.Close();

        await Task.WhenAll(_readerTask, _writerTask)
            .ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);

        _logger.LogInformation(
            "Stopped serial worker for {Function} on {Port}",
            _functionName,
            PortPath);
    }

    // ── Write ─────────────────────────────────────────────────────────────────


    public async Task<OperationResult> WriteAsync(
    byte[] data,
    CancellationToken token = default)
{
    _logger.LogDebug(
        "Queueing {ByteCount} bytes to {Function} on {Port}",
        data.Length,
        _functionName,
        PortPath);

    if (_port is null || !_port.IsOpen)
        return OperationResult.Failure(
            $"Serial port {PortPath} is not open");

    var enqueued = await EnqueueAsync(data, token)
        .ConfigureAwait(false);

    return enqueued
        ? OperationResult.Success()
        : OperationResult.Failure(
            $"Failed to enqueue data on {PortPath}");
}

    //public async Task<OperationResult> WriteAsync(byte[] data, CancellationToken token = default)
    //{
    //    _logger.LogDebug(
    //        "Writing {ByteCount} bytes to {Function} on {Port}",
    //    _functionName,
    //    data.Length,
    //    PortPath);

    //    var port = _port; // snapshot — avoids null ref if DisposeAsync runs concurrently
    //    if (port is null || !port.IsOpen)
    //        return OperationResult.Failure($"Serial port {PortPath} is not open");

    //    try
    //    {
    //        await port.BaseStream.WriteAsync(data, token).ConfigureAwait(false);
    //        _logger.LogInformation(
    //            "Completed serial write {Function} on {Port}: {Success}",
    //            _functionName,
    //            PortPath,
    //            true);
    //        return OperationResult.Success();
    //    }
    //    catch (OperationCanceledException)
    //    {
    //        return OperationResult.Failure("Operation cancelled");
    //    }
    //    catch (Exception ex)
    //    {
    //        _logger.LogWarning(ex,
    //            "Error on serial write {Function} on {Port}: {Success}",
    //            _functionName,
    //            PortPath,
    //            false);
    //        return OperationResult.Failure($"Write failed on {PortPath}: {ex.Message}");
    //    }



    //    //Queuing system
    //    //if (_sendQueue.Writer.TryWrite(data))
    //    //{
    //    //    return OperationResult.Success();
    //    //}
    //    //else
    //    //{
    //    //    return OperationResult.Failure($"Write queue for Serial port {_port} is full");
    //    //}
    //    //Queue if _sendQueue is full
    //    //var enqueued = await EnqueueAsync(data, token).ConfigureAwait(false);
    //    //return enqueued ? OperationResult.Success() : OperationResult.Failure($"Failed to Enqueue data on {PortPath}");

    //}

    private async Task<bool> EnqueueAsync(byte[] data, CancellationToken token)
    {
        try
        {
            await _sendQueue.Writer.WriteAsync(data, token).ConfigureAwait(false);
            return true;
        }
        catch (OperationCanceledException) { return false; }
        catch (ChannelClosedException) { return false; }
    }

  //  public Task<OperationResult> WriteTextAsync(string text, CancellationToken token = default)
    //    => WriteAsync(Encoding.UTF8.GetBytes(text), token);

    public Task<OperationResult> WriteTextAsync(string text, CancellationToken token = default)
    => WriteAsync(Encoding.ASCII.GetBytes(text), token);

    // ── Read loop (serial → channel) ─────────────────────────────────────────

    private async Task<OperationResult> ReadLoopAsync(CancellationToken token)
    {

        var buffer = new byte[4096];

        try
        {
            _logger.LogInformation(
                "Read loop started for {Function} on  {Port}",
                _functionName,
                PortPath);

            while (!token.IsCancellationRequested && _port?.IsOpen == true)
            {
                int count = await _port.BaseStream
                    .ReadAsync(buffer, token)
                    .ConfigureAwait(false);

                if (count <= 0) continue;

              //  var text = Encoding.UTF8.GetString(buffer, 0, count);
                var text = Encoding.ASCII.GetString(buffer, 0, count);
                _logger.LogDebug(
                    "RAW {Function} BYTES: {Bytes}",
                    _functionName,
                    BitConverter.ToString(buffer, 0, count));

                await ProcessIncomingTextAsync(text, token).ConfigureAwait(false);
            }
            return OperationResult.Success();
        }
        catch (OperationCanceledException) {
            return OperationResult.Success();
        }
        catch (ObjectDisposedException)    {
            return OperationResult.Success();
        }
        catch (IOException ex)
        {
            _logger.LogError(ex,
                "Read failure for {Function} on {Port}",
                _functionName,
                PortPath);
            return OperationResult.Failure($"Read error on {PortPath}. Exception: {ex.Message}");
        }
        finally
        {
            _logger.LogInformation(
            "Message channel completed for {Function} on {Port}",
            _functionName,
            PortPath);
            _received.Writer.TryComplete();

        }
    }

    //private async Task ProcessIncomingTextAsync(string chunk, CancellationToken token)
    //{
    //    _lineBuffer.Append(chunk);

    //    while (true)
    //    {
    //        var current = _lineBuffer.ToString();
    //        var nl = current.IndexOf('\n');
    //        if (nl < 0) break;

    //        var line = current[..nl].TrimEnd('\r');
    //        _lineBuffer.Remove(0, nl + 1);

    //      //  var functionName = _registry.GetFunctionName(PortPath);
    //     //   var primaryFunction = functionNames.Count > 0 ? functionNames[0] : PortPath;

    //        var message = new SerialMessage
    //        {
    //            FunctionName = _functionName,
    //            PortPath     = PortPath,
    //            Payload      = Encoding.UTF8.GetBytes(line),
    //            Text         = line
    //        };



    //        _logger.LogDebug(
    //            "Received line for {Function} on {Port}: {Line}",
    //            _functionName,
    //            PortPath,
    //            line);
    //        await _received.Writer.WriteAsync(message, token).ConfigureAwait(false);
    //    }
    //}

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
                catch (Exception ex) when(ex is not OperationCanceledException)
{
    _logger.LogWarning(ex, "Write error on {Port}", PortPath);
}
            }
        }
        catch (OperationCanceledException) { }
    }

    private async Task ProcessIncomingTextAsync(string chunk, CancellationToken token)
    {
        _lineBuffer.Append(chunk);


      //  ROTATOR has custom framing
        if (_functionName == Feature.RotatorName)
        {
            _logger.LogDebug("Customer processing for {FunctionName}", _functionName);
            await ProcessRotatorFramesAsync(token)
                .ConfigureAwait(false);

            return;
        }


        while (true)
        {
            var current = _lineBuffer.ToString();

  
            // =========================================================
            // DEFAULT framing
            // Standard newline-delimited protocols
            // =========================================================

            int nl = current.IndexOf('\n');

            if (nl < 0)
                return;

            string defaultLine = current[..nl].TrimEnd('\r');

            _lineBuffer.Remove(0, nl + 1);

            var defaultMessage = new SerialMessage
            {
                FunctionName = _functionName,
                PortPath = PortPath,
             //   Payload = Encoding.UTF8.GetBytes(defaultLine),
                Payload = Encoding.ASCII.GetBytes(defaultLine),
                Text = defaultLine
            };

            _logger.LogDebug(
                "Received line for {Function} on {Port}: {Line}",
                _functionName,
                PortPath,
                defaultLine);

            await _received.Writer
                .WriteAsync(defaultMessage, token)
                .ConfigureAwait(false);
        }
    }

    private bool TryParseRotatorFrame(
    string frame,
    out string normalized,
    out string error)
    {
        normalized = string.Empty;
        error = string.Empty;

        frame = frame.Trim();

        // Minimum valid size
        if (frame.Length < 10)
        {
            error = "Frame too short";
            return false;
        }

        // -------------------------------------------------
        // HEADER
        // -------------------------------------------------

        string header = frame[..1];

        if (!Rotator.Headers.Any(h =>
            h.Equals(header, StringComparison.OrdinalIgnoreCase)))
        {
            error = $"Invalid header '{header}'";
            return false;
        }

        // -------------------------------------------------
        // TERMINATOR
        // -------------------------------------------------

        string terminator = frame[^1..];

        if (!Rotator.Terminators.Any(t =>
            t.Equals(terminator, StringComparison.OrdinalIgnoreCase)))
        {
            error = $"Invalid terminator '{terminator}'";
            return false;
        }

        // -------------------------------------------------
        // NODE
        // -------------------------------------------------

        string node = frame.Substring(1, 1);

        if (!node.Equals(Rotator.Node,
            StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogWarning(
                "ROTATOR: replacing invalid node '{Old}' with '{New}'",
                node,
                Rotator.Node);

            node = Rotator.Node;
        }

        // -------------------------------------------------
        // COMMAND
        // -------------------------------------------------

        string command = frame.Substring(2, 3);

        if (!Rotator.Commands.Any(c =>
            c.Equals(command,
                StringComparison.OrdinalIgnoreCase)))
        {
            error = $"Invalid command '{command}'";
            return false;
        }

        // -------------------------------------------------
        // NUMERIC
        // -------------------------------------------------

        string digits = frame.Substring(5, 4);

        if (!int.TryParse(digits, out _))
        {
            error = $"Invalid numeric block '{digits}'";
            return false;
        }

        // -------------------------------------------------
        // NORMALIZE
        // -------------------------------------------------

        normalized =
            $"{header.ToUpper()}" +
            $"{Rotator.Node.ToUpper()}" +
            $"{command.ToUpper()}" +
            $"{digits}" +
            $"{terminator.ToUpper()}";

        return true;
    }
    private async Task ProcessRotatorFramesAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            var current = _lineBuffer.ToString();

            if (string.IsNullOrWhiteSpace(current))
                return;

            // =========================================================
            // 1. FIND HEADER
            // =========================================================

            int start = -1;

            foreach (var header in Rotator.Headers)
            {
                int idx = current.IndexOf(
                    header,
                    StringComparison.OrdinalIgnoreCase);

                if (idx >= 0 && (start < 0 || idx < start))
                {
                    start = idx;
                }
            }

            if (start < 0)
            {
                _logger.LogError(
                    "ROTATOR: no valid header found. Discarding buffer: '{Buffer}'",
                    current.Replace("\r", "\\r").Replace("\n", "\\n"));

                _lineBuffer.Clear();
                return;
            }

            if (start > 0)
            {
                _logger.LogWarning(
                    "ROTATOR: discarding noise before frame: '{Noise}'",
                    current[..start].Replace("\r", "\\r").Replace("\n", "\\n"));

                _lineBuffer.Remove(0, start);
                current = _lineBuffer.ToString();
            }

            // =========================================================
            // 2. FIND TERMINATOR (back-to-front scan)
            // =========================================================

            int terminatorIndex = -1;

            for (int i = current.Length - 1; i >= 0; i--)
            {
                char ch = current[i];

                if (Rotator.Terminators.Contains(ch.ToString()))
                {
                    terminatorIndex = i;
                    break;
                }
            }

            if (terminatorIndex < 0)
            {
                _logger.LogDebug(
                    "ROTATOR: waiting for terminator. Buffer: '{Buffer}'",
                    current.Replace("\r", "\\r").Replace("\n", "\\n"));

                return;
            }

            // =========================================================
            // 3. EXTRACT FRAME
            // =========================================================

            string frame = current[..(terminatorIndex + 1)];

            int consumeUntil = terminatorIndex + 1;

            while (consumeUntil < current.Length &&
                   (current[consumeUntil] == '\r' || current[consumeUntil] == '\n'))
            {
                consumeUntil++;
            }

            _lineBuffer.Remove(0, consumeUntil);

            frame = frame.Trim();

            if (string.IsNullOrWhiteSpace(frame))
                return;

            // =========================================================
            // 4. VALIDATE FRAME
            // =========================================================

            if (!TryParseRotatorFrame(
                    frame,
                    out var normalized,
                    out var error))
            {
                _logger.LogError(
                    "ROTATOR: discarding invalid frame '{Frame}': {Error}",
                    frame,
                    error);

                return;
            }

            // =========================================================
            // 5. ACCEPTED FRAME
            // =========================================================

            _logger.LogDebug(
                "ROTATOR: accepted frame '{Frame}'",
                normalized);

            var message = new SerialMessage
            {
                FunctionName = _functionName,
                PortPath = PortPath,
                Payload = Encoding.ASCII.GetBytes(normalized),
                Text = normalized
            };

            await _received.Writer
                .WriteAsync(message, token)
                .ConfigureAwait(false);
        }
    }

    // ── Disposal ──────────────────────────────────────────────────────────────

    public async ValueTask DisposeAsync()
    {
        _logger.LogDebug(
            "Disposing serial worker for {Function} on {Port}",
            _functionName,
            PortPath);
        await StopAsync().ConfigureAwait(false);
        _cts.Dispose();
        _port?.Dispose();
        _port = null;
    }


}
