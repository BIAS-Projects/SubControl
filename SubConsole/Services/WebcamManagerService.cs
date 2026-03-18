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

    // Secondary index: display name → device-path id currently streaming.
    // Prevents two workers for the same physical camera (e.g. mfdevice1 and
    // mfdevice2 both named "USB Camera") from running simultaneously.
    private readonly ConcurrentDictionary<string, string> _nameToActiveId = new();

    // Per-name lock so Start/Stop for the same camera name are serialised.
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _nameLocks = new();

    private CancellationToken _appToken;

    public WebcamManagerService(ILogger<WebcamManagerService> logger)
    {
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _appToken = stoppingToken;
        _logger.LogInformation("WebcamManager started");
        try { await Task.Delay(Timeout.Infinite, stoppingToken); }
        catch (OperationCanceledException) { }
    }

    public async Task<bool> StartWebcamAsync(Gst.Device device, string host, int port,
                                              string? capsHint = null, bool capsHintNeedsGrayConvert = false)
    {
        var id = device.PathString;
        var name = device.DisplayName ?? "Unknown";

        // Per-name serialisation: only one Start/Stop for the same camera name
        // at a time, preventing duplicate workers when GStreamer fires multiple
        // mfdeviceN events for the same physical camera.
        var nameLock = _nameLocks.GetOrAdd(name, _ => new SemaphoreSlim(1, 1));
        await nameLock.WaitAsync(_appToken);
        try
        {
            // Reject if already running under this exact device path
            if (_workers.ContainsKey(id))
            {
                _logger.LogWarning("Webcam already running: {Name} ({Id})", name, id);
                return false;
            }

            // Reject if another device path for the same display name is already
            // streaming — this is the duplicate-MF-device guard.
            if (_nameToActiveId.TryGetValue(name, out var activeId) && _workers.ContainsKey(activeId))
            {
                _logger.LogWarning(
                    "Webcam already running for camera '{Name}' under different id {ActiveId} — ignoring {NewId}",
                    name, activeId, id);
                return false;
            }

            var worker = new WebcamWorker(
                device: device,
                host: host,
                port: port,
                logger: _logger,
                capsHint: capsHint,
                capsHintNeedsGrayConvert: capsHintNeedsGrayConvert);

            if (!_workers.TryAdd(id, worker))
            {
                await worker.DisposeAsync();
                return false;
            }

            _nameToActiveId[name] = id;
            await worker.StartAsync(_appToken);
            _logger.LogInformation("Webcam started: {Name} on port {Port}", name, port);
            return true;
        }
        finally
        {
            nameLock.Release();
        }
    }

    public async Task StopWebcamAsync(string deviceId)
    {
        if (!_workers.TryGetValue(deviceId, out var peekWorker))
        {
            _logger.LogWarning("StopWebcamAsync: no worker found for {DeviceId}", deviceId);
            return;
        }

        // Serialise under the name lock so Stop and a concurrent Start don't race
        var name = peekWorker.DeviceName;
        var nameLock = _nameLocks.GetOrAdd(name, _ => new SemaphoreSlim(1, 1));
        await nameLock.WaitAsync();
        try
        {
            if (_workers.TryRemove(deviceId, out var worker))
            {
                _nameToActiveId.TryRemove(name, out _);
                await worker.DisposeAsync();
                _logger.LogInformation("Webcam stopped: {DeviceId}", deviceId);
            }
        }
        finally
        {
            nameLock.Release();
        }
    }

    public bool IsRunning(string deviceId) => _workers.ContainsKey(deviceId);

    public IEnumerable<string> RunningDeviceIds => _workers.Keys;

    /// <summary>Returns the caps string cached by a running worker, or null.</summary>
    public (string? caps, bool isGray) GetCachedCaps(string deviceId)
    {
        if (_workers.TryGetValue(deviceId, out var w))
            return (w.CachedCaps, w.CachedNeedsGrayConvert);
        return (null, false);
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("Stopping all webcams ({Count})", _workers.Count);

        var workers = _workers.ToArray();
        var disposeTasks = workers.Select(kv => kv.Value.DisposeAsync().AsTask()).ToArray();
        await Task.WhenAll(disposeTasks);

        _workers.Clear();
        _nameToActiveId.Clear();

        await base.StopAsync(cancellationToken);
        _logger.LogInformation("All webcams stopped");
    }
}