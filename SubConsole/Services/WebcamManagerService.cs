using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System.Collections.Concurrent;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace SubConsole.Services;

public class WebcamManagerService : BackgroundService
{
    private readonly ILogger<WebcamManagerService> _logger;
    private readonly ConcurrentDictionary<string, WebcamWorker> _workers = new();
    private CancellationToken _appToken;

    public WebcamManagerService(ILogger<WebcamManagerService> logger)
    {
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _appToken = stoppingToken;
        _logger.LogInformation("WebcamManager started");
        try
        {
            await Task.Delay(Timeout.Infinite, stoppingToken);
        }
        catch (OperationCanceledException) { }
    }

    public async Task<bool> StartWebcamAsync(Gst.Device device, string host, int port)
    {
        var id = device.PathString;
        var name = device.DisplayName ?? "Unknown";

        if (_workers.ContainsKey(id))
        {
            _logger.LogWarning("Webcam already running: {Name} ({Id})", name, id);
            return false;
        }

        var worker = new WebcamWorker(
            device: device,
            host: host,
            port: port,
            logger: _logger);

        if (!_workers.TryAdd(id, worker))
            return false;

        await worker.StartAsync(_appToken);
        _logger.LogInformation("Webcam started: {Name} on port {Port}", name, port);
        return true;
    }

    public async Task StopWebcamAsync(string deviceId)
    {
        if (_workers.TryRemove(deviceId, out var worker))
        {
            await worker.StopAsync();
            await worker.DisposeAsync();
            _logger.LogInformation("Webcam stopped: {DeviceId}", deviceId);
        }
        else
        {
            _logger.LogWarning("StopWebcamAsync: no worker found for {DeviceId}", deviceId);
        }
    }

    public bool IsRunning(string deviceId) => _workers.ContainsKey(deviceId);

    public IEnumerable<string> RunningDeviceIds => _workers.Keys;

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("Stopping all webcams ({Count})", _workers.Count);

        var tasks = _workers.Values.Select(w => w.StopAsync()).ToArray();
        await Task.WhenAll(tasks);

        foreach (var worker in _workers.Values)
            await worker.DisposeAsync();

        _workers.Clear();

        await base.StopAsync(cancellationToken);
        _logger.LogInformation("All webcams stopped");
    }
}