using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System.Collections.Concurrent;

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
}