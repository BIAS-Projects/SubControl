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
    private readonly SubControlMAUI.Services.SQLiteService? _db;
    private readonly ConcurrentDictionary<string, WebcamWorker> _workers = new();

    // Secondary index: display name → device-path id currently streaming.
    private readonly ConcurrentDictionary<string, string> _nameToActiveId = new();

    // Per-name lock so Start/Stop for the same camera name are serialised.
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _nameLocks = new();

    // Tracks which (deviceId → port) each worker is using so streams can be
    // restarted on a new host IP without losing port assignments.
    private readonly ConcurrentDictionary<string, int> _workerPorts = new();

    // The host IP currently being streamed to.  Defaults to loopback so streams
    // are safe even with no client connected; updated when a TCP client connects.
    private string _currentHost = "127.0.0.1";

    private CancellationToken _appToken;

    public WebcamManagerService(ILogger<WebcamManagerService> logger,
                                SubControlMAUI.Services.SQLiteService? db = null)
    {
        _logger = logger;
        _db = db;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _appToken = stoppingToken;
        _logger.LogInformation("WebcamManager started");
        try { await Task.Delay(Timeout.Infinite, stoppingToken); }
        catch (OperationCanceledException) { }
    }

    // ------------------------------------------------------------------ //
    //  Stream redirection                                                  //
    // ------------------------------------------------------------------ //

    /// <summary>
    /// Redirects all active video streams to <paramref name="newHost"/>.
    /// Each worker is stopped and restarted on the same port with the new
    /// destination IP.  Caps are cached in the worker so no re-probing occurs.
    /// Called by TcpHostService when a client connects or disconnects.
    /// </summary>
    public async Task RedirectStreamsAsync(string newHost)
    {
        if (newHost == _currentHost)
            return;

        _logger.LogInformation("Redirecting video streams from {OldHost} to {NewHost}",
            _currentHost, newHost);

        _currentHost = newHost;

        // Snapshot the current workers — their devices, ports, and cached caps
        var snapshot = _workers
            .Select(kv =>
            {
                _workerPorts.TryGetValue(kv.Key, out int port);
                return new
                {
                    DeviceId = kv.Key,
                    Port = port,
                    Device = kv.Value.Device,
                    CachedCaps = kv.Value.CachedCaps,
                    CachedIsGray = kv.Value.CachedNeedsGrayConvert,
                };
            })
            .ToList();

        // Stop all current workers in parallel
        var stopTasks = snapshot.Select(s => StopWebcamAsync(s.DeviceId)).ToArray();
        await Task.WhenAll(stopTasks);

        // Restart each on the new host, reusing cached caps so no re-probing occurs
        foreach (var s in snapshot)
        {
            if (s.Device == null || s.Port == 0) continue;

            await StartWebcamAsync(
                device: s.Device,
                host: newHost,
                port: s.Port,
                capsHint: s.CachedCaps,
                capsHintNeedsGrayConvert: s.CachedIsGray);
        }

        _logger.LogInformation("All streams redirected to {NewHost}", newHost);
    }

    // ------------------------------------------------------------------ //
    //  Start / Stop                                                        //
    // ------------------------------------------------------------------ //

    public async Task<bool> StartWebcamAsync(Gst.Device device, string host, int port,
                                              string? capsHint = null, bool capsHintNeedsGrayConvert = false)
    {
        var id = device.PathString;
        var name = device.DisplayName ?? "Unknown";

        var nameLock = _nameLocks.GetOrAdd(name, _ => new SemaphoreSlim(1, 1));
        await nameLock.WaitAsync(_appToken);
        try
        {
            if (_workers.ContainsKey(id))
            {
                _logger.LogWarning("Webcam already running: {Name} ({Id})", name, id);
                return false;
            }

            if (_nameToActiveId.TryGetValue(name, out var activeId) && _workers.ContainsKey(activeId))
            {
                _logger.LogWarning(
                    "Webcam already running for camera '{Name}' under different id {ActiveId} — ignoring {NewId}",
                    name, activeId, id);
                return false;
            }

            if (_db != null)
            {
                var providerType = SubControlMAUI.Services.SQLiteService.ProviderTypeFromDevicePath(id);
                if (await _db.IsPermanentlyUnsupportedAsync(name, providerType))
                {
                    _logger.LogWarning(
                        "Skipping {Name} on {Provider} — previously marked as permanently unsupported. " +
                        "Delete the database record to re-enable.",
                        name, providerType);
                    return false;
                }
            }

            var worker = new WebcamWorker(
                device: device,
                host: host,
                port: port,
                logger: _logger,
                capsHint: capsHint,
                capsHintNeedsGrayConvert: capsHintNeedsGrayConvert,
                db: _db);

            if (!_workers.TryAdd(id, worker))
            {
                await worker.DisposeAsync();
                return false;
            }

            _nameToActiveId[name] = id;
            _workerPorts[id] = port;
            await worker.StartAsync(_appToken);
            _logger.LogInformation("Webcam started: {Name} → {Host}:{Port}", name, host, port);
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

        var name = peekWorker.DeviceName;
        var nameLock = _nameLocks.GetOrAdd(name, _ => new SemaphoreSlim(1, 1));
        await nameLock.WaitAsync();
        try
        {
            if (_workers.TryRemove(deviceId, out var worker))
            {
                _nameToActiveId.TryRemove(name, out _);
                _workerPorts.TryRemove(deviceId, out _);
                await worker.DisposeAsync();
                _logger.LogInformation("Webcam stopped: {DeviceId}", deviceId);
            }
        }
        finally
        {
            nameLock.Release();
        }
    }

    // ------------------------------------------------------------------ //
    //  Queries                                                             //
    // ------------------------------------------------------------------ //

    public bool IsRunning(string deviceId) => _workers.ContainsKey(deviceId);

    public IEnumerable<string> RunningDeviceIds => _workers.Keys;

    /// <summary>
    /// Returns a list of strings describing each active stream, formatted as
    /// "CameraName,Port" — e.g. "HD User Facing,5001".
    /// Useful for sending the stream manifest to a connecting client so it knows
    /// which UDP port to open for each camera.
    /// </summary>
    public List<string> GetStreamInfo()
    {
        return _workers
            .Values
            .Select(w =>
            {
                _workerPorts.TryGetValue(w.Device.PathString, out int port);
                return $"{w.DeviceName},{port}";
            })
            .OrderBy(s => s)
            .ToList();
    }

    /// <summary>Returns the caps string cached by a running worker, or null.</summary>
    public (string? caps, bool isGray) GetCachedCaps(string deviceId)
    {
        if (_workers.TryGetValue(deviceId, out var w))
            return (w.CachedCaps, w.CachedNeedsGrayConvert);
        return (null, false);
    }

    // ------------------------------------------------------------------ //
    //  Shutdown                                                            //
    // ------------------------------------------------------------------ //

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("Stopping all webcams ({Count})", _workers.Count);

        var workers = _workers.ToArray();
        var disposeTasks = workers.Select(kv => kv.Value.DisposeAsync().AsTask()).ToArray();
        await Task.WhenAll(disposeTasks);

        _workers.Clear();
        _nameToActiveId.Clear();
        _workerPorts.Clear();

        await base.StopAsync(cancellationToken);
        _logger.LogInformation("All webcams stopped");
    }
}