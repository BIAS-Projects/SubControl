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

        if (_workers.ContainsKey(id))
        {
            _logger.LogWarning("Webcam already running: {id}", id);
            return false;
        }

        var worker = new WebcamWorker(device, host, port, _logger);

        if (!_workers.TryAdd(id, worker))
            return false;

        await worker.StartAsync(_appToken);

        _logger.LogInformation("Webcam started: {id} on port {port}", id, port);
        return true;
    }

    public async Task StopWebcamAsync(string deviceId)
    {
        if (_workers.TryRemove(deviceId, out var worker))
        {
            await worker.StopAsync();
            await worker.DisposeAsync();
            _logger.LogInformation("Webcam stopped: {deviceId}", deviceId);
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("Stopping all webcams");

        var tasks = _workers.Values.Select(w => w.StopAsync()).ToArray();
        await Task.WhenAll(tasks);

        foreach (var worker in _workers.Values)
            await worker.DisposeAsync();

        _workers.Clear();

        await base.StopAsync(cancellationToken);
    }
}
