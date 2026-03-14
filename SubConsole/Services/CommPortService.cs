using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System.IO.Ports;
using System.Text;
using System.Threading.Channels;

namespace SubConsole.Services
{
    public class CommPortService : BackgroundService
    {
        private readonly ILogger<CommPortService> _logger;

        private readonly SemaphoreSlim _portLock = new(1, 1);

        private SerialPort? _port;

        private readonly Channel<string> _channel;

        private readonly StringBuilder _lineBuffer = new();

        public ChannelReader<string> Reader => _channel.Reader;

        public bool IsOpen => _port?.IsOpen == true;

        public CommPortService(ILogger<CommPortService> logger)
        {
            _logger = logger;

            _channel = Channel.CreateUnbounded<string>(
                new UnboundedChannelOptions
                {
                    SingleWriter = true,
                    SingleReader = false
                });
        }

        // ---------------- BACKGROUND LOOP ----------------

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            var buffer = new byte[1024];

            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    if (_port == null || !_port.IsOpen)
                    {
                        await Task.Delay(200, stoppingToken);
                        continue;
                    }
                }
                catch(OperationCanceledException)
                {
                    break;
                }

                try
                {
                    int bytesRead = await _port.BaseStream.ReadAsync(
                        buffer,
                        0,
                        buffer.Length,
                        stoppingToken);

                    if (bytesRead > 0)
                    {
                        string text = Encoding.UTF8.GetString(buffer, 0, bytesRead);

                        await ProcessIncomingData(text, stoppingToken);
                    }
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Serial read error");
                    await Task.Delay(1000, stoppingToken);
                }
            }
        }

        // ---------------- OPEN PORT ----------------

        public async Task<bool> OpenAsync(string portName, int baudRate)
        {
            await _portLock.WaitAsync();

            try
            {
                if (_port != null && _port.IsOpen)
                {
                    _logger.LogWarning("Serial port already open");
                    return false;
                }

                _port = new SerialPort(portName, baudRate)
                {
                    Encoding = Encoding.UTF8,
                    ReadTimeout = 500,
                    WriteTimeout = 500
                };

                _port.Open();

                _logger.LogInformation("Serial port {port} opened", portName);

                return true;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to open serial port");
                return false;
            }
            finally
            {
                _portLock.Release();
            }
        }

        // ---------------- CLOSE PORT ----------------

        public async Task CloseAsync()
        {
            await _portLock.WaitAsync();

            try
            {
                if (_port == null)
                    return;

                if (_port.IsOpen)
                    _port.Close();

                _port.Dispose();
                _port = null;

                _logger.LogInformation("Serial port closed");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error closing serial port");
            }
            finally
            {
                _portLock.Release();
            }
        }

        // ---------------- WRITE ----------------

        public async Task<bool> WriteAsync(string data, CancellationToken token = default)
        {
            await _portLock.WaitAsync(token);

            try
            {
                if (_port == null || !_port.IsOpen)
                    return false;

                byte[] bytes = Encoding.UTF8.GetBytes(data);

                await _port.BaseStream.WriteAsync(bytes, token);

                return true;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Serial write failed");
                return false;
            }
            finally
            {
                _portLock.Release();
            }
        }

        // ---------------- DATA PROCESSING ----------------

        private async Task ProcessIncomingData(string data, CancellationToken token)
        {
            _lineBuffer.Append(data);

            while (true)
            {
                string current = _lineBuffer.ToString();

                int newlineIndex = current.IndexOf('\n');

                if (newlineIndex < 0)
                    break;

                string line = current[..newlineIndex].Trim('\r');

                _lineBuffer.Remove(0, newlineIndex + 1);

                await _channel.Writer.WriteAsync(line, token);

                _logger.LogInformation("RX: {line}", line);
            }
        }

        // ---------------- CLEAN SHUTDOWN ----------------

        public override async Task StopAsync(CancellationToken cancellationToken)
        {
            await CloseAsync();

            _channel.Writer.TryComplete();

            await base.StopAsync(cancellationToken);
        }
    }
}