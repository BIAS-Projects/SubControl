using System.IO.Ports;
using System.Text;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;

namespace SubConsole.Services;

public class SerialPortWorker
{
    private readonly string _portName;
    private readonly int _baudRate;
    private readonly ILogger _logger;

    private SerialPort? _port;

    private CancellationTokenSource _cts = new();

    private Task? _readerTask;

    private readonly Channel<string> _channel =
       Channel.CreateBounded<string>(1000);

    private readonly StringBuilder _lineBuffer = new();

    public ChannelReader<string> Reader => _channel.Reader;

    public bool IsOpen => _port?.IsOpen == true;

    public SerialPortWorker(string portName, int baudRate, ILogger logger)
    {
        _portName = portName;
        _baudRate = baudRate;
        _logger = logger;
    }

    // ---------------- START ----------------

    public async Task StartAsync(CancellationToken parentToken)
    {
        _port = new SerialPort(_portName, _baudRate)
        {
            WriteTimeout = 2000,
            ReadTimeout = 2000,
            Encoding = Encoding.UTF8
        };

        _port.Handshake = Handshake.None;
   //     _port.RtsEnable = false;
   //     _port.DtrEnable = true;
        _port.Open();

        _logger.LogInformation("Serial {port} opened", _portName);

        var linked = CancellationTokenSource.CreateLinkedTokenSource(parentToken, _cts.Token);

        _readerTask = Task.Run(() => ReadLoop(linked.Token), linked.Token);

        await Task.CompletedTask;
    }

    // ---------------- READ LOOP ----------------

    private async Task ReadLoop(CancellationToken token)
    {
        var buffer = new byte[1024];

        try
        {
            while (!token.IsCancellationRequested && _port?.IsOpen == true)
            {
                int bytes = await _port.BaseStream.ReadAsync(buffer, token);

                if (bytes <= 0)
                    continue;

                var text = Encoding.UTF8.GetString(buffer, 0, bytes);

                await ProcessIncoming(text, token);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (ObjectDisposedException)
        {
            // Happens when port is closed during shutdown
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Serial read error on {port}", _portName);
        }
    }

    // ---------------- PROCESS DATA ----------------

    private async Task ProcessIncoming(string data, CancellationToken token)
    {
        _lineBuffer.Append(data);

        while (true)
        {
            var current = _lineBuffer.ToString();

            int newline = current.IndexOf('\n');

            if (newline < 0)
                break;

            var line = current[..newline].Trim('\r');

            _lineBuffer.Remove(0, newline + 1);

            await _channel.Writer.WriteAsync(line, token);
        }
    }

    // ---------------- WRITE ----------------

    public async Task WriteAsync(string text, CancellationToken token)
    {
        if (_port == null || !_port.IsOpen)
            return;

        var bytes = Encoding.UTF8.GetBytes(text);

        await _port.BaseStream.WriteAsync(bytes, token);
        Console.WriteLine($"Wrote {text} to {_portName}");
    }

    // ---------------- STOP ----------------

    public async Task StopAsync()
    {
        try
        {
            // Optional: send STOP or safe command
            if (_port?.IsOpen == true)
            {
                await WriteAsync("$PBLUTP,S,PWR,CTRL,OFF,15*67\r\n", CancellationToken.None);
            }


            _cts.Cancel();

            // Critical: close port BEFORE awaiting read loop
            if (_port?.IsOpen == true)
                _port.Close();

            if (_readerTask != null)
                await _readerTask;
        }
        catch (Exception)
        {
            // Ignore shutdown exceptions
        }
        finally
        {
            _port?.Dispose();
            _port = null;

            _channel.Writer.TryComplete();

            _logger.LogInformation("Serial {port} closed", _portName);
        }
    }
}