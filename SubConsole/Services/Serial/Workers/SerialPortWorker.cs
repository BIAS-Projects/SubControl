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
        "Queueing {data} bytes to {Function} on {Port}",
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
                    _logger.LogDebug("Writing {data} to {PortPath} for function {_functionName}", data, PortPath, _functionName);
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
        out string error,
        out bool homed)
    {
        normalized = string.Empty;
        error = string.Empty;
        homed = false;

        frame = frame.Trim();

        if (frame.Length != 10)
        {
            error = $"Invalid frame length {frame.Length}, expected 10";
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
        // NODE
        // -------------------------------------------------

        string node = frame.Substring(1, 1);

        if (!node.Equals(Rotator.Node, StringComparison.OrdinalIgnoreCase))
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
            c.Equals(command, StringComparison.OrdinalIgnoreCase)))
        {
            error = $"Invalid command '{command}'";
            return false;
        }

        // -------------------------------------------------
        // TERMINATOR — must be R, r, W, or w at position 9
        // -------------------------------------------------

        string terminator = frame[9..];

        bool isRead = terminator.Equals("R", StringComparison.OrdinalIgnoreCase);
        bool isWrite = terminator.Equals("W", StringComparison.OrdinalIgnoreCase);

        if (!isRead && !isWrite)
        {
            error = $"Invalid terminator '{terminator}' at position 9, expected R/r or W/w";
            return false;
        }

        homed = char.IsUpper(terminator[0]);

        // -------------------------------------------------
        // PAYLOAD — positions 5-8 (4 chars)
        // MRV responses carry a version string (e.g. "J1.0") instead of
        // a zero-padded integer.  Accept any 4-char ASCII payload for MRV;
        // all other commands must have a numeric payload.
        // -------------------------------------------------

        string digits = frame.Substring(5, 4);

        bool isMrv = command.Equals("MRV", StringComparison.OrdinalIgnoreCase);

        if (!isMrv && !int.TryParse(digits, out _))
        {
            error = $"Invalid numeric block '{digits}'";
            return false;
        }

        // For MRV, verify the payload contains only printable ASCII
        // (guards against garbage bytes being accepted as a version string)
        if (isMrv && !digits.All(c => c >= 0x20 && c <= 0x7E))
        {
            error = $"Invalid version block '{digits}' — non-printable characters";
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
            $"{terminator}";   // preserve original case — encodes homed state

        return true;
    }

    private async Task ProcessRotatorFramesAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            var current = _lineBuffer.ToString();

            _logger.LogDebug(
                "ROTATOR: buffer ({Length} chars): '{Buffer}'",
                current.Length,
                current.Replace("\r", "\\r").Replace("\n", "\\n"));

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
                    start = idx;
            }

            if (start < 0)
            {
                _logger.LogError(
                    "ROTATOR: no valid header found, discarding buffer: '{Buffer}'",
                    current.Replace("\r", "\\r").Replace("\n", "\\n"));

                _lineBuffer.Clear();
                return;
            }

            if (start > 0)
            {
                _logger.LogWarning(
                    "ROTATOR: discarding noise before header: '{Noise}'",
                    current[..start].Replace("\r", "\\r").Replace("\n", "\\n"));

                _lineBuffer.Remove(0, start);
                current = _lineBuffer.ToString();
            }

            // =========================================================
            // 2. WAIT UNTIL WE HAVE A FULL 10-CHAR FRAME
            //    Frame = header(1) + node(1) + command(3) + digits(4) + terminator(1)
            // =========================================================

            if (current.Length < 10)
            {
                _logger.LogDebug(
                    "ROTATOR: only {Length} chars in buffer, waiting for full 10-char frame",
                    current.Length);
                return;
            }

            // =========================================================
            // 3. EXTRACT EXACTLY 10 CHARACTERS
            // =========================================================

            string frame = current[..10];

            // =========================================================
            // 4. VALIDATE TERMINATOR IS R/r/W/w AT POSITION 9
            //    If not, the frame is misaligned — discard one char and retry
            // =========================================================

            char terminatorChar = frame[9];

            if (terminatorChar != 'R' && terminatorChar != 'r' &&
                terminatorChar != 'W' && terminatorChar != 'w')
            {
                _logger.LogWarning(
                    "ROTATOR: invalid terminator '{Char}' at position 9, " +
                    "frame misaligned — discarding header and resyncing",
                    terminatorChar);

                // Discard the bad header byte and loop to resync
                _lineBuffer.Remove(0, 1);
                continue;
            }

            // =========================================================
            // 5. CONSUME THE FRAME + ANY TRAILING CR/LF
            // =========================================================

            int consumeUntil = 10;

            while (consumeUntil < current.Length &&
                   (current[consumeUntil] == '\r' || current[consumeUntil] == '\n'))
            {
                consumeUntil++;
            }

            _lineBuffer.Remove(0, consumeUntil);

            // =========================================================
            // 6. VALIDATE AND PARSE
            // =========================================================

            if (!TryParseRotatorFrame(frame, out var normalized, out var error, out var homed))
            {
                _logger.LogError(
                    "ROTATOR: discarding invalid frame '{Frame}': {Error}",
                    frame,
                    error);

                // Don't return — loop to process any remaining buffer data
                continue;
            }

            // =========================================================
            // 7. EMIT ACCEPTED FRAME
            // =========================================================

            _logger.LogDebug(
                "ROTATOR: accepted frame '{Frame}' | homed={Homed}",
                normalized,
                homed);

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

            // Loop to handle any further complete frames in the buffer
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
