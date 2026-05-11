
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Serilog.Core;
using SubConsole.Models;
using SubConsole.Services.Helpers;
using SubConsole.Services.Serial;
using SubConsole.Services.Video;
using SubControlMAUI.Models;
using System.Collections;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using static SQLite.SQLite3;
using static SubConsole.Models.UsbDeviceInfo;
using static System.Runtime.InteropServices.JavaScript.JSType;

namespace SubConsole.Services.TCP;

/// <summary>
/// TCP server on port 9000.
///
/// Each connected client gets three independent loops:
///   ReceiveLoop — socket bytes → framed commands → Incoming channel
///   ProcessLoop — Incoming channel → TcpSerialCommandHandler → Outgoing channel
///   SendLoop    — merges Outgoing (ACK/response) and Push (serial RX) → socket
///
/// Push frames carry the FunctionName of the port the data arrived on.
/// The client uses this to pair device replies with the commands it sent.
///
/// Wire protocol (UTF-8, EOM = "\n"):
///   Client → Server:  {id}|VERB[|arg...]
///   Server → Client:  ACK|{id}                    (immediate)
///                     {id}|{json result}           (handler response)
///                     PUSH|{functionName}|{text}   (serial device data, any time)
/// </summary>
public sealed class TcpHostService : BackgroundService
{
    private readonly ILogger<TcpHostService> _logger;
    private readonly TcpSerialCommandHandler _serialHandler;
    private readonly TcpCameraCommandHandler _cameraHandler;  
    private readonly SerialPushPump _pushPump;
    private readonly ISerialPortManagerService _serialPortManager;
    private readonly ICameraManagerService _cameraManager;   

    private readonly TcpListener _listener;
    private readonly ConcurrentDictionary<Guid, ClientState> _clients = new();
    private readonly int _port;

    public TcpHostService(
        ILogger<TcpHostService> logger,
        TcpSerialCommandHandler serialHandler,
        TcpCameraCommandHandler cameraHandler,   // ← new
        SerialPushPump pushPump,
        ISerialPortManagerService serialPortManager,
        ICameraManagerService cameraManager,   // ← new
        IOptions<TcpSettings> tcpSettings)
    {
        _logger = logger;
        _serialHandler = serialHandler;
        _cameraHandler = cameraHandler;
        _pushPump = pushPump;
        _serialPortManager = serialPortManager;
        _cameraManager = cameraManager;

        _port = tcpSettings.Value.Port;
        if (_port <= 0)
        {
            _logger.LogError("Invalid TCP port configuration port: {Port}", _port);
            throw new InvalidOperationException("Invalid TCP port configuration");
        }

        _listener = new TcpListener(IPAddress.Any, _port);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {

   //     await BasicTest(stoppingToken);


        _logger.LogInformation("TCP server starting on port {Port}", _port);
        _listener.Start();
        _logger.LogInformation("TCP server started on port {Port}", _port);

        // ── Wire serial port-change notifications (unchanged) ─────────────────
        UsbPortRegistry.Instance.PortChanged += OnPortChanged;
        stoppingToken.Register(() =>
            UsbPortRegistry.Instance.PortChanged -= OnPortChanged);

        // ── Wire camera change notifications ──────────────────────────────────
        UsbCameraRegistry.Instance.CameraChanged += OnCameraChanged;   // ← new
        stoppingToken.Register(() =>
            UsbCameraRegistry.Instance.CameraChanged -= OnCameraChanged);

        _logger.LogDebug("Port and camera change listeners registered");

        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                // Run both registry refreshes in parallel on each accept cycle
                await Task.WhenAll(
                    UsbPortRegistry.Instance.RefreshAsync(stoppingToken),
                    UsbCameraRegistry.Instance.RefreshAsync(stoppingToken));   // ← new

                var client = await _listener.AcceptTcpClientAsync(stoppingToken);
                var id = Guid.NewGuid();

                _logger.LogInformation(
                    "Client {ClientId} connected from {RemoteEndPoint}. Active clients: {ClientCount}",
                    id, client.Client.RemoteEndPoint, _clients.Count + 1);

                var pushChannel = _pushPump.AddClient(id);
                var state = new ClientState(id, client, pushChannel);
                _clients[id] = state;

                _ = Task.Run(() => ReceiveLoop(state, stoppingToken), stoppingToken);
                _ = Task.Run(() => ProcessLoop(state, stoppingToken), stoppingToken);
                _ = Task.Run(() => SendLoop(state, stoppingToken), stoppingToken);
            }
        }
        catch (OperationCanceledException) { }
        finally
        {
            _logger.LogInformation("TCP server shutting down");
            _listener.Stop();
            _logger.LogInformation("TCP server stopped");
        }
    }

    private void OnCameraChanged(object? sender, CameraChangedEventArgs e)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                var result = await _cameraManager.HandleCameraChangedAsync(e);

                if (result.IsSuccess && result.DeviceId is null)
                {
                    _logger.LogDebug(
                        "Camera {Kind} — no registered camera affected",
                        e.Kind);
                    return;
                }

                _logger.LogInformation(
                    "Camera {Kind}: device {DeviceId} stream '{StreamPath}' — {Status}",
                    result.Kind,
                    result.DeviceId,
                    result.StreamPathName,
                    result.IsSuccess ? "OK" : result.Error);

                var frame = new PushFrame
                {
                    FunctionName = "SYSTEM",
                    Text = JsonSerializer.Serialize(new
                    {
                        type = "CAMERA_CHANGED",
                        kind = result.Kind.ToString(),
                        deviceId = result.DeviceId,
                        streamPathName = result.StreamPathName,
                        error = result.Error,
                        timestamp = DateTimeOffset.UtcNow
                    })
                };

                _logger.LogDebug(
                    "Broadcasting camera change for {DeviceId} to {ClientCount} clients",
                    result.DeviceId, _clients.Count);

                BroadcastToAllClients(frame);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "Camera change handler failed for {FriendlyName}", e.Camera.FriendlyName);
            }
        });
    }

    private void OnPortChanged(object? sender, PortChangedEventArgs e)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                var result = await _serialPortManager.HandlePortChangedAsync(e);

                if (result.IsNoOp)
                {
                    _logger.LogDebug(
                        "Port {Kind} on {Port} — no registered function affected",
                        e.Kind, e.Port.PortName);
                    return;
                }

                _logger.LogInformation(
                    "Port {ChangeKind}: function {FunctionName} changed from {OldPort} to {NewPort}",
                    result.Kind,
                    result.Function,
                    result.OldPort,
                    result.NewPort ?? "(removed)");

                // Build the push frame and broadcast to all connected clients
                var frame = new PushFrame
                {
                    FunctionName = "SYSTEM",
                    Text = System.Text.Json.JsonSerializer.Serialize(new
                    {
                        type = "PORT_CHANGED",
                        kind = result.Kind.ToString(),
                        function = result.Function,
                        oldPort = result.OldPort,
                        newPort = result.NewPort,
                        error = result.Error,
                        timestamp = DateTimeOffset.UtcNow
                    })
                };

                _logger.LogDebug(
                    "Broadcasting port change for {FunctionName} to {ClientCount} clients",
                    result.Function,
                    _clients.Count);

                BroadcastToAllClients(frame);

            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Port change handler failed for {Port}", e.Port.PortName);
            }
        });
    }

    private void BroadcastToAllClients(PushFrame frame)
    {
        var clientCount = _clients.Count;

        foreach (var client in _clients.Values)
        {



            if (!client.PushChannel.Writer.TryWrite(frame))
            {

                _logger.LogDebug("TryWrite to client {ClientId}: {Result}", client.Id);

                _logger.LogWarning(
                    "Push channel full for client {ClientId} — notification dropped (Function={FunctionName})",
                    client.Id,
                    frame.FunctionName);
            }
        }

        _logger.LogDebug(
            "Broadcast complete for {FunctionName} to {ClientCount} clients",
            frame.FunctionName,
            clientCount);
    }

    // ── Receive loop (socket → Incoming channel) ──────────────────────────────

    private async Task ReceiveLoop(ClientState state, CancellationToken token)
    {
        var stream = state.Client.GetStream();
        var buffer = new byte[4096];
        var sb     = new StringBuilder();

        try
        {
            while (!token.IsCancellationRequested)
            {
                int bytesRead = await stream.ReadAsync(buffer, token);
                if (bytesRead == 0) break; // clean disconnect

                sb.Append(Encoding.UTF8.GetString(buffer, 0, bytesRead));
                var current = sb.ToString();

                int eomIdx;
                while ((eomIdx = current.IndexOf(TcpProtocol.EOM, StringComparison.Ordinal)) >= 0)
                {
                    var frame = current[..eomIdx];
                    current   = current[(eomIdx + TcpProtocol.EOM.Length)..];

                    // Frame format: {id}|{command}
                    var sepIdx  = frame.IndexOf(TcpProtocol.SEP, StringComparison.Ordinal);
                    var id      = sepIdx >= 0 ? frame[..sepIdx]       : frame;
                    var command = sepIdx >= 0 ? frame[(sepIdx + 1)..] : string.Empty;

                    _logger.LogDebug(
                        "Client {ClientId} received request {RequestId} ({CommandLength} chars)",
                        state.Id,
                        id,
                        command.Length);

                    await state.Incoming.Writer.WriteAsync(new IncomingFrame(id, command), token);
                }

                sb.Clear();
                sb.Append(current);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Client {ClientId} receive error", state.Id);
        }
        finally
        {
            state.Incoming.Writer.TryComplete();
            CleanupClient(state.Id);
        }
    }

    // ── Process loop (Incoming → handler → Outgoing) ──────────────────────────

    private async Task ProcessLoop(ClientState state, CancellationToken token)
    {
        await foreach (var frame in state.Incoming.Reader.ReadAllAsync(token))
        {
            _logger.LogDebug(
                "Client {ClientId} processing request {RequestId}",
                state.Id, frame.Id);

            try
            {
                await state.Outgoing.Writer.WriteAsync(
                    $"{TcpProtocol.ACK}{TcpProtocol.SEP}{frame.Id}{TcpProtocol.EOM}", token);

                var message = JsonSerializer.Deserialize<TCPMessageBody<string>>(frame.Command);

                if (message is null)
                    throw new InvalidOperationException($"Invalid JSON: {frame.Command}");

                // Route to the correct domain handler based on the Function field.
                // Serial commands use a device function name or "TOM".
                // Camera commands use the reserved function name "CAMERA".

                //var result = message.Function.Equals("CAMERA", StringComparison.OrdinalIgnoreCase)
                var result = message.Function.Contains("CAMERA")
                    ? await _cameraHandler.HandleAsync(message, token)
                    : await _serialHandler.HandleAsync(message, token);

                await state.Outgoing.Writer.WriteAsync(
                    $"{frame.Id}{TcpProtocol.SEP}{result}{TcpProtocol.EOM}", token);

                _logger.LogInformation(
                    "Client {ClientId} completed request {RequestId}",
                    state.Id, frame.Id);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "Client {ClientId} failed request {RequestId}",
                    state.Id, frame.Id);

                await state.Outgoing.Writer.WriteAsync(
                    $"{TcpProtocol.NACK}{TcpProtocol.SEP}{frame.Id}{TcpProtocol.SEP}{ex.Message}{TcpProtocol.EOM}",
                    token);
            }
        }
    }

    // ── Send loop (Outgoing + Push → socket) ──────────────────────────────────

    private async Task SendLoop(ClientState state, CancellationToken token)
    {
        var stream = state.Client.GetStream();

        // Merge response frames and push frames into a single ordered send path.
        var merged = Channel.CreateUnbounded<string>(
            new UnboundedChannelOptions { SingleWriter = false, SingleReader = true });

        _ = Task.Run(async () =>
        {
            await foreach (var msg in state.Outgoing.Reader.ReadAllAsync(token))
                await merged.Writer.WriteAsync(msg, token);
        }, token);

        _ = Task.Run(async () =>
        {
            await foreach (var push in state.PushChannel.Reader.ReadAllAsync(token))
                await merged.Writer.WriteAsync(push.ToWireFrame(TcpProtocol.EOM), token);
        }, token);

        try
        {
            _logger.LogDebug(
                "Client {ClientId} send loop started",
                state.Id);

            await foreach (var message in merged.Reader.ReadAllAsync(token))
            {
                if (!state.Client.Connected) break;

                _logger.LogDebug(
                    "Client {ClientId} sending {ByteCount} bytes",
                    state.Id,
                    message.Length);

                var bytes = Encoding.UTF8.GetBytes(message);
                await stream.WriteAsync(bytes, token);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Client {ClientId} send error", state.Id);
        }
        finally
        {
            _logger.LogDebug(
                "Client {ClientId} send loop stopped",
                state.Id);
            CleanupClient(state.Id);
        }
    }

    // ── Cleanup ───────────────────────────────────────────────────────────────

    private void CleanupClient(Guid id)
    {
        if (!_clients.TryRemove(id, out var state)) return;

        _pushPump.RemoveClient(id);

        state.Incoming.Writer.TryComplete();
        state.Outgoing.Writer.TryComplete();
        state.PushChannel.Complete();

        try { state.Client.Close(); } catch { }

        _logger.LogInformation(
            "Client {ClientId} disconnected. Active clients: {ClientCount}",
            id,
            _clients.Count);
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("Stopping TCP host service. Disconnecting {ClientCount} clients",
        _clients.Count);

        foreach (var id in _clients.Keys.ToList())
            CleanupClient(id);

        _listener.Stop();
        await base.StopAsync(cancellationToken);
    }

    // ── Inner types ───────────────────────────────────────────────────────────

    private sealed class ClientState
    {
        public Guid              Id          { get; }
        public TcpClient         Client      { get; }
        public ClientPushChannel PushChannel { get; }

        public Channel<IncomingFrame> Incoming { get; } =
            Channel.CreateBounded<IncomingFrame>(new BoundedChannelOptions(100)
            { FullMode = BoundedChannelFullMode.Wait });

        public Channel<string> Outgoing { get; } =
            Channel.CreateBounded<string>(new BoundedChannelOptions(100)
            { FullMode = BoundedChannelFullMode.Wait });

        public ClientState(Guid id, TcpClient client, ClientPushChannel pushChannel)
        {
            Id          = id;
            Client      = client;
            PushChannel = pushChannel;
        }
    }

    private sealed record IncomingFrame(string Id, string Command);



    public async Task BasicTest(CancellationToken stoppingToken)
    {
        //TEST  TEST  TEST  TEST  TEST  TEST  TEST  TEST  TEST  TEST  TEST  TEST  TEST  TEST  TEST  TEST  TEST  

        string key = $"::USB\\VID_CAFE\u0026PID_4001\u0026MI_00\\7\u0026B7F3F1C\u00260\u00260000";
        string function = "TOM Input";
        string key2 = $"0403:6011:FTDIBUS\\VID_0403\u002BPID_6011\u002BBP04126-01A\\0000:BP04126-01A";
        string function2 = "TOM Output";
        string key3 = $"::USB\\VID_09CB\u0026PID_4007\u0026MI_02\\7\u00261C0DEC35\u00260\u00260002";
        string function3 = "FLIR";
        string function4 = "CAMERA";

        Console.WriteLine("LIST DEVICES");
        TCPMessageBody<string> command = new TCPMessageBody<string>("TOM", "LIST DEVICES", "");
        var result = await _serialHandler.HandleAsync(command, stoppingToken);
        Console.WriteLine(result);
        Console.WriteLine("");
        Console.WriteLine("LIST REGISTERED - EMPTY LIST");
        command = new TCPMessageBody<string>("TOM", "LIST REGISTERED", "");
        result = await _serialHandler.HandleAsync(command, stoppingToken);
        Console.WriteLine(result);
        Console.WriteLine("");
        Console.WriteLine("REGISTER");
        FunctionToPortEntry entry = new FunctionToPortEntry()
        {
            DeviceKey = key,
            FunctionName = function,
            BaudRate = "115200",
            WorkerType = SerialWorkerType.Text.ToString()
        };
        var json = JsonSerializer.Serialize<FunctionToPortEntry>(entry);
        command = new TCPMessageBody<string>("TOM", "REGISTER", json);
        result = await _serialHandler.HandleAsync(command, stoppingToken);
        Console.WriteLine(result);
        Console.WriteLine("");
        Console.WriteLine("REGISTER THE SAME THING TWICE - OVERWRITE BASED ON KEY - NOT DUPLICATE");
        command = new TCPMessageBody<string>("TOM", "REGISTER", json);
        result = await _serialHandler.HandleAsync(command, stoppingToken);
        Console.WriteLine(result);
        Console.WriteLine("");
        Console.WriteLine("LIST REGISTERED");
        command = new TCPMessageBody<string>("TOM", "LIST REGISTERED", "");
        result = await _serialHandler.HandleAsync(command, stoppingToken);
        Console.WriteLine(result);
        Console.WriteLine("");
        Console.WriteLine("REGISTER THE SAME KEY TO A DIFFERENT FUNCTION - UPDATED KEY ENTRY");
        entry = new FunctionToPortEntry()
        {
            DeviceKey = key,
            FunctionName = "test",
            BaudRate = "115200",
            WorkerType = SerialWorkerType.Text.ToString()
        };
        json = JsonSerializer.Serialize<FunctionToPortEntry>(entry);
        command = new TCPMessageBody<string>("TOM", "REGISTER", json);
        result = await _serialHandler.HandleAsync(command, stoppingToken);
        Console.WriteLine(result);
        Console.WriteLine("");
        Console.WriteLine("LIST REGISTERED");
        command = new TCPMessageBody<string>("TOM", "LIST REGISTERED", "");
        result = await _serialHandler.HandleAsync(command, stoppingToken);
        Console.WriteLine(result);
        Console.WriteLine("");
        Console.WriteLine("REGISTER THE SAME KEY TO A DIFFERENT FUNCTION - RESTORE ENTRY");
        entry = new FunctionToPortEntry()
        {
            DeviceKey = key,
            FunctionName = function,
            BaudRate = "115200",
            WorkerType = SerialWorkerType.Text.ToString()
        };
        json = JsonSerializer.Serialize<FunctionToPortEntry>(entry);
        command = new TCPMessageBody<string>("TOM", "REGISTER", json);
        result = await _serialHandler.HandleAsync(command, stoppingToken);
        Console.WriteLine(result);

        Console.WriteLine("");
        Console.WriteLine("LIST REGISTERED");
        command = new TCPMessageBody<string>("TOM", "LIST REGISTERED", "");
        result = await _serialHandler.HandleAsync(command, stoppingToken);
        Console.WriteLine(result);
        Console.WriteLine("");
        Console.WriteLine("UNREGISTER");
        command = new TCPMessageBody<string>("TOM", "UNREGISTER", function);
        result = await _serialHandler.HandleAsync(command, stoppingToken);
        Console.WriteLine(result);
        Console.WriteLine("");
        Console.WriteLine("UNREGISTER SOMETHNG ALREADY UNREGISTERED");
        command = new TCPMessageBody<string>("TOM", "UNREGISTER", function);
        result = await _serialHandler.HandleAsync(command, stoppingToken);
        Console.WriteLine(result);
        Console.WriteLine("");
        Console.WriteLine("LIST REGISTERED");
        command = new TCPMessageBody<string>("TOM", "LIST REGISTERED", "");
        result = await _serialHandler.HandleAsync(command, stoppingToken);
        Console.WriteLine(result);
        Console.WriteLine("");
        Console.WriteLine("REGISTER");
        //entry = new FunctionToPortEntry()
        //{
        //    DeviceKey = "0403:6011:FTDIBUS\\VID_0403+PID_6011+BP04126-01A\\0000:BP04126-01A",
        //    FunctionName = "TOM Input",
        //    BaudRate = "115200",
        //    WorkerType = SerialWorkerType.Text.ToString()
        //};
        // json = JsonSerializer.Serialize<FunctionToPortEntry>(entry);
        command = new TCPMessageBody<string>("TOM", "REGISTER", json);
        result = await _serialHandler.HandleAsync(command, stoppingToken);
        Console.WriteLine(result);
        Console.WriteLine("");
        Console.WriteLine("OPEN COMM ");
        command = new TCPMessageBody<string>("TOM", "OPEN", function);
        result = await _serialHandler.HandleAsync(command, stoppingToken);
        Console.WriteLine(result);
        Console.WriteLine("");
        Console.WriteLine("OPEN COMM  TWICE");
        command = new TCPMessageBody<string>("TOM", "OPEN", function);
        result = await _serialHandler.HandleAsync(command, stoppingToken);
        Console.WriteLine(result);
        Console.WriteLine("");
        Console.WriteLine("CLOSE COMM ");
        command = new TCPMessageBody<string>("TOM", "CLOSE", function);
        result = await _serialHandler.HandleAsync(command, stoppingToken);
        Console.WriteLine(result);
        Console.WriteLine("");
        Console.WriteLine("CLOSE COMM  TWICE");
        command = new TCPMessageBody<string>("TOM", "CLOSE", function);
        result = await _serialHandler.HandleAsync(command, stoppingToken);
        Console.WriteLine(result);
        Console.WriteLine("");
        Console.WriteLine("WRITE TEXT TO CLOSED PORT");
        command = new TCPMessageBody<string>(function, "WRITE TEXT", TOM.TurnOnAllSystemsCommand);
        result = await _serialHandler.HandleAsync(command, stoppingToken);
        Console.WriteLine(result);
        Console.WriteLine("");
        Console.WriteLine("OPEN COMM ");
        command = new TCPMessageBody<string>("TOM", "OPEN", function);
        result = await _serialHandler.HandleAsync(command, stoppingToken);
        Console.WriteLine(result);
        Console.WriteLine("");
        //Console.WriteLine("WRITE TEXT TURN TOM OFF");
        //command = new TCPMessageBody<string>(function, "WRITE TEXT", TOM.TurnOffAllSystemsCommand);
        //result = await _handler.HandleAsync(command, stoppingToken);
        //Console.WriteLine(result);
        Console.WriteLine("");
        Console.WriteLine("WRITE TEXT TURN TOM ON");
        command = new TCPMessageBody<string>(function, "WRITE TEXT", TOM.TurnOnAllSystemsCommand);
        result = await _serialHandler.HandleAsync(command, stoppingToken);
        Console.WriteLine(result);
        Console.WriteLine("");
        FunctionToPortEntry entry2 = new FunctionToPortEntry()
        {
            DeviceKey = key2,
            FunctionName = function2,
            BaudRate = "115200",
            WorkerType = SerialWorkerType.Text.ToString()
        };
        var json2 = JsonSerializer.Serialize<FunctionToPortEntry>(entry2);

        Console.WriteLine("REGISTER TOM OUPUT");
        command = new TCPMessageBody<string>("TOM", "REGISTER", json2);
        result = await _serialHandler.HandleAsync(command, stoppingToken);
        Console.WriteLine(result);
        Console.WriteLine("");
        Console.WriteLine("OPEN TOM OUPUT ");
        command = new TCPMessageBody<string>("TOM", "OPEN", function2);
        result = await _serialHandler.HandleAsync(command, stoppingToken);
        Console.WriteLine(result);
        Console.WriteLine("");

        Console.WriteLine("");
        Console.WriteLine("REGISTER FLIR");
        entry = new FunctionToPortEntry()
        {
            DeviceKey = key3,
            FunctionName = function3,
            BaudRate = "921600",
            WorkerType = SerialWorkerType.Flir.ToString()
        };
        json = JsonSerializer.Serialize<FunctionToPortEntry>(entry);
        command = new TCPMessageBody<string>("TOM", "REGISTER", json);
        result = await _serialHandler.HandleAsync(command, stoppingToken);
        Console.WriteLine(result);
        Console.WriteLine("");
        Console.WriteLine("OPEN FLIR ");
        command = new TCPMessageBody<string>("TOM", "OPEN", function3);
        result = await _serialHandler.HandleAsync(command, stoppingToken);
        Console.WriteLine(result);
        Console.WriteLine("");
        Console.WriteLine("WRITE FLIR RAINBOW");
        command = new TCPMessageBody<string>(function3, "WRITE TEXT", FLIR.LUTtoRAINBOW);
        result = await _serialHandler.HandleAsync(command, stoppingToken);
        Console.WriteLine(result);
        Console.WriteLine("");

        Console.WriteLine("");
        Console.WriteLine("CHECK FFMPEG");
        command = new TCPMessageBody<string>(function4, "CHECK FFMPEG","");
        result = await _cameraHandler.HandleAsync(command, stoppingToken);
        Console.WriteLine(result);
        Console.WriteLine("");
        Console.WriteLine("");
        Console.WriteLine("CHECK MTX VERSION");
        command = new TCPMessageBody<string>(function4, "CHECK MTX VERSION", "");
        result = await _cameraHandler.HandleAsync(command, stoppingToken);
        Console.WriteLine(result);
        Console.WriteLine("");
        Console.WriteLine("");
        Console.WriteLine("CHECK MTX STREAMS");
        command = new TCPMessageBody<string>(function4, "CHECK MTX STREAMS", "");
        result = await _cameraHandler.HandleAsync(command, stoppingToken);
        Console.WriteLine(result);
        Console.WriteLine("");

        Console.WriteLine("");

        string deviceId = "USB\\VID_32E4&PID_9422&MI_00\\7&149655b1&0&0000";
        string deviceId2 = "USB\\VID_09CB&PID_4007&MI_00\\7&1C0DEC35&0&0000";
        const string cameraFunction = "CAMERA";

        // ── LIST CAMERAS ──────────────────────────────────────────────────────────

        Console.WriteLine("LIST CAMERAS");
        command = new TCPMessageBody<string>(cameraFunction, "LIST CAMERAS", "");
        result = await _cameraHandler.HandleAsync(command, stoppingToken);
        Console.WriteLine(result);
        Console.WriteLine("");

        // ── LIST REGISTERED — empty ───────────────────────────────────────────────

        Console.WriteLine("LIST REGISTERED - EMPTY");
        command = new TCPMessageBody<string>(cameraFunction, "LIST REGISTERED", "");
        result = await _cameraHandler.HandleAsync(command, stoppingToken);
        Console.WriteLine(result);
        Console.WriteLine("");

        // ── REGISTER USB CAMERA ───────────────────────────────────────────────────

        Console.WriteLine("REGISTER USB CAMERA");
        var register = new CameraRegistrationRequest
        {
            DeviceId = deviceId,
            StreamPathName = "usbcamera",
            FfmpegOptions = new FfmpegCameraOptions
            {
                DeviceName = "USB Camera",          // Windows dshow name
                Width = 1280,
                Height = 720,
                Framerate = 30,
                PixelFormat = "yuv420p",
                VideoCodec = "libx264",
                Preset = "ultrafast",
                Tune = "zerolatency",
                Bitrate = "4M"
            },
            MtxConfig = new MediaMtxPathConfig
            {
                RunOnDemandRestart = true,
                RunOnDemandStartTimeout = "10s",
                RunOnDemandCloseAfter = "10s"
            }
        };
        var camjson = JsonSerializer.Serialize(register);
        command = new TCPMessageBody<string>(cameraFunction, "REGISTER", json);
        Console.WriteLine($"Function: {command.Function}");
        Console.WriteLine($"Command:  {command.Command}");
        Console.WriteLine($"Data:     {command.Data}");

        result = await _cameraHandler.HandleAsync(command, stoppingToken);
        Console.WriteLine(result);
        Console.WriteLine("");

        // ── REGISTER SAME CAMERA TWICE — should overwrite, not duplicate ──────────

        Console.WriteLine("REGISTER SAME CAMERA TWICE - OVERWRITE");
        command = new TCPMessageBody<string>(cameraFunction, "REGISTER", json);
        result = await _cameraHandler.HandleAsync(command, stoppingToken);
        Console.WriteLine(result);
        Console.WriteLine("");

        // ── REGISTER FLIR CAMERA ──────────────────────────────────────────────────

        Console.WriteLine("REGISTER FLIR CAMERA");
        var registerFlir = new CameraRegistrationRequest
        {
            DeviceId = deviceId2,
            StreamPathName = "flir",
            FfmpegOptions = new FfmpegCameraOptions
            {
                DeviceName = "FLIR Video",          // Windows dshow name
                Width = 640,
                Height = 512,
                Framerate = 9,
                PixelFormat = "yuv420p",
                VideoCodec = "libx264",
                Preset = "ultrafast",
                Tune = "zerolatency",
                Bitrate = "2M"
            },
            MtxConfig = new MediaMtxPathConfig
            {
                RunOnDemandRestart = true,
                RunOnDemandStartTimeout = "10s",
                RunOnDemandCloseAfter = "10s"
            }
        };
        var jsonFlir = JsonSerializer.Serialize(registerFlir);
        command = new TCPMessageBody<string>(cameraFunction, "REGISTER", jsonFlir);
        result = await _cameraHandler.HandleAsync(command, stoppingToken);
        Console.WriteLine(result);
        Console.WriteLine("");

        // ── LIST REGISTERED — should show both cameras ────────────────────────────

        Console.WriteLine("LIST REGISTERED - TWO CAMERAS");
        command = new TCPMessageBody<string>(cameraFunction, "LIST REGISTERED", "");
        result = await _cameraHandler.HandleAsync(command, stoppingToken);
        Console.WriteLine(result);
        Console.WriteLine("");

        // ── ADD STREAM — push USB camera path config to MediaMTX ─────────────────

        Console.WriteLine("ADD STREAM - USB CAMERA");
        command = new TCPMessageBody<string>(cameraFunction, "ADD STREAM", deviceId);
        result = await _cameraHandler.HandleAsync(command, stoppingToken);
        Console.WriteLine(result);
        Console.WriteLine("");

        // ── ADD STREAM TWICE — should return failure, already active ─────────────

        Console.WriteLine("ADD STREAM TWICE - SHOULD FAIL");
        command = new TCPMessageBody<string>(cameraFunction, "ADD STREAM", deviceId);
        result = await _cameraHandler.HandleAsync(command, stoppingToken);
        Console.WriteLine(result);
        Console.WriteLine("");

        // ── ADD STREAM — push FLIR path config to MediaMTX ───────────────────────

        Console.WriteLine("ADD STREAM - FLIR");
        command = new TCPMessageBody<string>(cameraFunction, "ADD STREAM", deviceId2);
        result = await _cameraHandler.HandleAsync(command, stoppingToken);
        Console.WriteLine(result);
        Console.WriteLine("");

        // ── UPDATE FFMPEG OPTIONS — change USB camera resolution ──────────────────

        Console.WriteLine("UPDATE FFMPEG - CHANGE RESOLUTION TO 1920x1080");
        var updateFfmpeg = new UpdateFfmpegRequest
        {
            DeviceId = deviceId,
            FfmpegOptions = new FfmpegCameraOptions
            {
                DeviceName = "USB Camera",
                Width = 1920,
                Height = 1080,
                Framerate = 30,
                PixelFormat = "yuv420p",
                VideoCodec = "libx264",
                Preset = "ultrafast",
                Tune = "zerolatency",
                Bitrate = "8M"
            }
        };
        json = JsonSerializer.Serialize(updateFfmpeg);
        command = new TCPMessageBody<string>(cameraFunction, "UPDATE FFMPEG", json);
        result = await _cameraHandler.HandleAsync(command, stoppingToken);
        Console.WriteLine(result);
        Console.WriteLine("");

        // ── UPDATE MTX CONFIG — tighten the close-after timeout ──────────────────

        Console.WriteLine("UPDATE MTX CONFIG - CHANGE CLOSE AFTER TO 5s");
        var updateMtx = new UpdateMtxRequest
        {
            DeviceId = deviceId,
            MtxConfig = new MediaMtxPathConfig
            {
                RunOnDemandRestart = true,
                RunOnDemandStartTimeout = "10s",
                RunOnDemandCloseAfter = "5s",
                Record = false
            }
        };
        json = JsonSerializer.Serialize(updateMtx);
        command = new TCPMessageBody<string>(cameraFunction, "UPDATE MTX", json);
        result = await _cameraHandler.HandleAsync(command, stoppingToken);
        Console.WriteLine(result);
        Console.WriteLine("");

        // ── UPDATE MTX CONFIG — enable recording ─────────────────────────────────

        Console.WriteLine("UPDATE MTX CONFIG - ENABLE RECORDING");
        var updateMtxRecord = new UpdateMtxRequest
        {
            DeviceId = deviceId,
            MtxConfig = new MediaMtxPathConfig
            {
                RunOnDemandRestart = true,
                RunOnDemandStartTimeout = "10s",
                RunOnDemandCloseAfter = "5s",
                Record = true,
                RecordPath = "./recordings/%path/%Y-%m-%d_%H-%M-%S-%f",
                RecordFormat = "fmp4",
                RecordSegmentDuration = "1h",
                RecordDeleteAfter = "7d"
            }
        };
        json = JsonSerializer.Serialize(updateMtxRecord);
        command = new TCPMessageBody<string>(cameraFunction, "UPDATE MTX", json);
        result = await _cameraHandler.HandleAsync(command, stoppingToken);
        Console.WriteLine(result);
        Console.WriteLine("");

        // ── DISCOVER — scan for known cameras and add any not yet in MTX ─────────

        Console.WriteLine("DISCOVER - AUTO ADD STREAMS");
        var discover = new CameraDiscoverRequest { AutoAdd = true };
        json = JsonSerializer.Serialize(discover);
        command = new TCPMessageBody<string>(cameraFunction, "DISCOVER", json);
        result = await _cameraHandler.HandleAsync(command, stoppingToken);
        Console.WriteLine(result);
        Console.WriteLine("");

        // ── DISCOVER — scan only, do not push to MTX ─────────────────────────────

        Console.WriteLine("DISCOVER - SCAN ONLY");
        var discoverScan = new CameraDiscoverRequest { AutoAdd = false };
        json = JsonSerializer.Serialize(discoverScan);
        command = new TCPMessageBody<string>(cameraFunction, "DISCOVER", json);
        result = await _cameraHandler.HandleAsync(command, stoppingToken);
        Console.WriteLine(result);
        Console.WriteLine("");

        // ── REMOVE STREAM — take USB camera offline in MTX ────────────────────────

        Console.WriteLine("REMOVE STREAM - USB CAMERA");
        command = new TCPMessageBody<string>(cameraFunction, "REMOVE STREAM", deviceId);
        result = await _cameraHandler.HandleAsync(command, stoppingToken);
        Console.WriteLine(result);
        Console.WriteLine("");

        // ── REMOVE STREAM TWICE — should succeed silently (nothing to do) ─────────

        Console.WriteLine("REMOVE STREAM TWICE - SHOULD SUCCEED (NO-OP)");
        command = new TCPMessageBody<string>(cameraFunction, "REMOVE STREAM", deviceId);
        result = await _cameraHandler.HandleAsync(command, stoppingToken);
        Console.WriteLine(result);
        Console.WriteLine("");

        // ── UNREGISTER — remove from registry and MTX ────────────────────────────

        Console.WriteLine("UNREGISTER USB CAMERA");
        command = new TCPMessageBody<string>(cameraFunction, "UNREGISTER", deviceId);
        result = await _cameraHandler.HandleAsync(command, stoppingToken);
        Console.WriteLine(result);
        Console.WriteLine("");

        // ── UNREGISTER ALREADY REMOVED — should return failure ───────────────────

        Console.WriteLine("UNREGISTER ALREADY UNREGISTERED - SHOULD FAIL");
        command = new TCPMessageBody<string>(cameraFunction, "UNREGISTER", deviceId);
        result = await _cameraHandler.HandleAsync(command, stoppingToken);
        Console.WriteLine(result);
        Console.WriteLine("");

        // ── UNREGISTER FLIR ───────────────────────────────────────────────────────

        Console.WriteLine("UNREGISTER FLIR");
        command = new TCPMessageBody<string>(cameraFunction, "UNREGISTER", deviceId2);
        result = await _cameraHandler.HandleAsync(command, stoppingToken);
        Console.WriteLine(result);
        Console.WriteLine("");

        // ── LIST REGISTERED — should be empty again ───────────────────────────────

        Console.WriteLine("LIST REGISTERED - SHOULD BE EMPTY");
        command = new TCPMessageBody<string>(cameraFunction, "LIST REGISTERED", "");
        result = await _cameraHandler.HandleAsync(command, stoppingToken);
        Console.WriteLine(result);
        Console.WriteLine("");

        // ── UNKNOWN COMMAND — should return error response ────────────────────────

        Console.WriteLine("UNKNOWN COMMAND - SHOULD RETURN ERROR");
        command = new TCPMessageBody<string>(cameraFunction, "EXPLODE", "");
        result = await _cameraHandler.HandleAsync(command, stoppingToken);
        Console.WriteLine(result);
        Console.WriteLine("");
    

    //TEST  TEST  TEST  TEST  TEST  TEST  TEST  TEST  TEST  TEST  TEST  TEST  TEST  TEST  TEST  TEST  TEST  

}
}
