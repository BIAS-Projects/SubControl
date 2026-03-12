using SubConsole.Models;
using System.IO.Ports;
using System.Management;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Channels;



namespace SubConsole.Services
{
    public class CommPortService
    {

        public CommPortService()
        {
            _channel = Channel.CreateUnbounded<string>(new UnboundedChannelOptions
            {
                SingleWriter = true,
                SingleReader = false
            });
        }

        public List<SerialDevice> GetSerialDevices()
        {

            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                return GetWindowsPorts();


            if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
                return GetLinuxPortsByID();

            return new List<SerialDevice>();
        }


        private List<SerialDevice> GetWindowsPorts()
        {
            List<SerialDevice> devices = new List<SerialDevice>();

            using var searcher = new ManagementObjectSearcher(
                "SELECT * FROM Win32_PnPEntity WHERE Name LIKE '%(COM%'");

            foreach (var device in searcher.Get())
            {
                var name = device["Name"]?.ToString();

                if (name == null)
                    continue;

                var start = name.LastIndexOf("(COM");
                var end = name.LastIndexOf(")");

                if (start >= 0 && end > start)
                {
                    var port = name.Substring(start + 1, end - start - 1);

                    devices.Add(new SerialDevice
                    {
                        Port = port,
                        Name = name,
                        DeviceId = device["DeviceID"]?.ToString()
                    });
                }
            }

            return devices;
        }


        private List<SerialDevice> GetLinuxPorts()
        {
            List<SerialDevice> devices = new List<SerialDevice>();

            var devPath = "/dev";

            var ports = Directory.GetFiles(devPath, "ttyUSB*")
                .Concat(Directory.GetFiles(devPath, "ttyACM*"));

            foreach (var port in ports)
            {
                devices.Add(new SerialDevice
                {
                    Port = port,
                    Name = Path.GetFileName(port),
                    DeviceId = port
                });
            }

            return devices;
        }

        private List<SerialDevice> GetLinuxPortsByID()
        {
            List<SerialDevice> devices = new List<SerialDevice>();
            var byIdPath = "/dev/serial/by-id";

            if (Directory.Exists(byIdPath))
            {
                foreach (var file in Directory.GetFiles(byIdPath))
                {
                    devices.Add(new SerialDevice
                    {
                        Port = Path.GetFullPath(file),
                        Name = Path.GetFileName(file),
                        DeviceId = file
                    });
                }
            }
            return devices;
        }








        private SerialPort _port;
        private readonly Channel<string> _channel;
        private readonly SemaphoreSlim _lifecycleLock = new(1, 1);
        private readonly StringBuilder _lineBuffer = new();

        private CancellationTokenSource _cts;

        public ChannelReader<string> Reader => _channel.Reader;



        public async Task StartAsync(string portName, int baudRate = 115200)
        {
            await _lifecycleLock.WaitAsync();

            try
            {
                if (_port != null)
                    return;

                _port = new SerialPort(portName, baudRate)
                {
                    Encoding = Encoding.UTF8
                };

                _port.Open();

                _cts = new CancellationTokenSource();

                _ = Task.Run(() => ReadLoop(_cts.Token));
            }
            finally
            {
                _lifecycleLock.Release();
            }
        }

        private async Task ReadLoop(CancellationToken token)
        {
            var buffer = new byte[1024];

            try
            {
                while (!token.IsCancellationRequested && _port.IsOpen)
                {
                    int bytesRead = await _port.BaseStream.ReadAsync(buffer, 0, buffer.Length, token);

                    if (bytesRead > 0)
                    {
                        string data = Encoding.UTF8.GetString(buffer, 0, bytesRead);
                        await ProcessIncomingData(data);
                    }
                }
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                await _channel.Writer.WriteAsync($"[ERROR] {ex.Message}");
            }
        }

        private async Task ProcessIncomingData(string data)
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

                await _channel.Writer.WriteAsync(line);
            }
        }

        public async Task StopAsync()
        {
            await _lifecycleLock.WaitAsync();

            try
            {
                if (_port == null)
                    return;

                _cts.Cancel();

                if (_port.IsOpen)
                    _port.Close();

                _port.Dispose();
                _port = null;

                _channel.Writer.TryComplete();
            }
            finally
            {
                _lifecycleLock.Release();
            }
        }
    


}
}