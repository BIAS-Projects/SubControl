using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System.Collections.Concurrent;
using System.IO.Ports;
using System.Runtime.InteropServices;

namespace SubConsole.Services;

public class SerialPortManagerService : BackgroundService
{
    private readonly ILogger<SerialPortManagerService> _logger;

    private readonly ConcurrentDictionary<string, SerialPortWorker> _ports = new();

    private CancellationToken _appToken;

    public IEnumerable<string> OpenPorts => _ports.Keys;

    public SerialPortManagerService(ILogger<SerialPortManagerService> logger)
    {
        _logger = logger;
    }

    // ---------------- BACKGROUND SERVICE ----------------

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("SerialPortManager started");

        _appToken = stoppingToken;

        try
        {
            await Task.Delay(Timeout.Infinite, stoppingToken);
        }
        catch (OperationCanceledException)
        {
        }
    }

    // ---------------- OPEN PORT ----------------

    public async Task<bool> OpenPortAsync(string port, int baudRate)
    {
        if (_ports.ContainsKey(port))
        {
            _logger.LogWarning("Port {port} already open", port);
            return false;
        }

        try
        {
            SerialPortWorker worker = new SerialPortWorker(port, baudRate, _logger);

            if (!_ports.TryAdd(port, worker))
                return false;

            await worker.StartAsync(_appToken);

            _logger.LogInformation("Opened port {port}", port);

            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError($"Error opening {port} : {ex.Message}");
            return false;
        }

    }

    // ---------------- CLOSE PORT ----------------

    public async Task ClosePortAsync(string port)
    {
        if (_ports.TryRemove(port, out var worker))
        {
            await worker.StopAsync();

            _logger.LogInformation("Closed port {port}", port);
        }
    }

    // ---------------- GET PORT ----------------

    public SerialPortWorker? GetPort(string port)
    {
        _ports.TryGetValue(port, out var worker);
        return worker;
    }

    // ---------------- SHUTDOWN ----------------

    public override async Task StopAsync(CancellationToken cancellationToken)
    {

        _logger.LogInformation("Stopping peripherals");

        //await CleanUpPeripherals(cancellationToken);

        _logger.LogInformation("Stopping SerialPortManager");

        var tasks = _ports.Values.Select(worker => worker.StopAsync());

        await Task.WhenAll(tasks);

        _ports.Clear();

        await base.StopAsync(cancellationToken);
    }

    //private async Task CleanUpPeripherals(CancellationToken cancellationToken)
    //{
    //    SerialPortWorker tomController =  GetPort("COM5");
    //    await tomController.WriteAsync("@\"$PBLUTP,S,PWR,CTRL,OFF,15*67\"", cancellationToken);

    //}


    // ---------------- LIST AVAILABLE PORTS ----------------
    private static readonly SemaphoreSlim _portListLock = new(1, 1);

    public static async Task<IReadOnlyList<string>> GetAvailablePortsAsync()
    {
        await _portListLock.WaitAsync();
        try
        {
            return await Task.Run(() =>
            {
                if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                    return GetWindowsPorts();

                if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
                    return GetLinuxPorts();

                return Array.Empty<string>();
            });
        }
        finally
        {
            _portListLock.Release();
        }
    }

    private static IReadOnlyList<string> GetWindowsPorts()
    {
        // SerialPort.GetPortNames() is reliable on Windows
        return SerialPort.GetPortNames()
                         .OrderBy(p => p)
                         .ToArray();
    }

    private static IReadOnlyList<string> GetLinuxPorts()
    {
        // Enumerate tty devices that typically represent serial ports
        var patterns = new[] { "ttyUSB*", "ttyACM*", "ttyS*", "ttyAMA*" };

        return patterns
            .SelectMany(pattern => Directory.GetFiles("/dev", pattern))
            .Where(IsSerialPortAccessible)
            .OrderBy(p => p)
            .ToArray();
    }

    private static bool IsSerialPortAccessible(string path)
    {
        try
        {
            // Quick open/close to verify the port is accessible and not a
            // phantom ttyS* entry that exists in /dev but has no hardware
            using var port = new SerialPort(path);
            port.Open();
            port.Close();
            return true;
        }
        catch
        {
            return false;
        }
    }
}