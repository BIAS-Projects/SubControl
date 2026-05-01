
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Serilog.Core;
using SubConsole.Models;
using SubConsole.Services.Helpers;
using SubConsole.Services.Serial;
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
    private readonly TcpSerialCommandHandler _handler;
    private readonly SerialPushPump _pushPump;

    private readonly TcpListener _listener;
    private readonly ConcurrentDictionary<Guid, ClientState> _clients = new();

    private readonly int _port;



    public TcpHostService(
        ILogger<TcpHostService> logger,
        TcpSerialCommandHandler handler,
        SerialPushPump pushPump,
        IOptions<TcpSettings> tcpSettings

           )
    {
        _logger = logger;
        _handler = handler;
        _pushPump = pushPump;


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
        _logger.LogInformation("TCP server starting on port {Port}", _port);

        _listener.Start();

        _logger.LogInformation("TCP server started on port {Port}", _port);

        BasicTest(stoppingToken);

        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                await UsbPortRegistry.Instance.RefreshAsync();

                var client = await _listener.AcceptTcpClientAsync(stoppingToken);
                var id = Guid.NewGuid();

                _logger.LogInformation(
                    "Client {ClientId} connected from {RemoteEndPoint}. Active clients: {ClientCount}",
                    id,
                    client.Client.RemoteEndPoint,
                    _clients.Count + 1);

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

                    _logger.LogDebug( "Client {ClientId} received frame {RequestId} with command {CommandPreview}",
                    state.Id,
                    id,
                    command.Length > 100 ? command[..100] : command);

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
            _logger.LogDebug("Client {ClientId} processing request {RequestId}",
            state.Id,
            frame.Id);

            try
            {
                // ACK immediately so the client's timeout is never hit by handler latency
                await state.Outgoing.Writer.WriteAsync(
                    $"{TcpProtocol.ACK}{TcpProtocol.SEP}{frame.Id}{TcpProtocol.EOM}", token);

                var message = JsonSerializer.Deserialize<TCPMessageBody<string>>(frame.Command);

                _logger.LogDebug("Client {ClientId} deserialized command {CommandType}",
                state.Id,
                message?.Command);

                if (message == null)
                {
                    throw new InvalidOperationException($"Invalid JSON message: {frame.Command}");
                }

                var result = await _handler.HandleAsync(message, token);


                await state.Outgoing.Writer.WriteAsync(
                    $"{frame.Id}{TcpProtocol.SEP}{result}{TcpProtocol.EOM}", token);

                _logger.LogInformation("Client {ClientId} successfully processed request {RequestId}",
                state.Id,
                frame.Id);

            }
            catch (Exception ex)
            {
                _logger.LogError(ex,"Client {ClientId} failed processing request {RequestId}. Command preview: {CommandPreview}",
                state.Id,
                frame.Id,
                frame.Command.Length > 100 ? frame.Command[..100] : frame.Command);

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
            await foreach (var message in merged.Reader.ReadAllAsync(token))
            {
                if (!state.Client.Connected) break;

                _logger.LogDebug("Client {ClientId} sending message of {Length} bytes",
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

        Console.WriteLine("LIST DEVICES");
        TCPMessageBody<string> command = new TCPMessageBody<string>("TOM", "LIST DEVICES", "");
        var result = await _handler.HandleAsync(command, stoppingToken);
        Console.WriteLine(result);
        Console.WriteLine("");
        Console.WriteLine("LIST REGISTERED - EMPTY LIST");
        command = new TCPMessageBody<string>("TOM", "LIST REGISTERED", "");
        result = await _handler.HandleAsync(command, stoppingToken);
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
        result = await _handler.HandleAsync(command, stoppingToken);
        Console.WriteLine(result);
        Console.WriteLine("");
        Console.WriteLine("REGISTER THE SAME THING TWICE - OVERWRITE BASED ON KEY - NOT DUPLICATE");
        command = new TCPMessageBody<string>("TOM", "REGISTER", json);
        result = await _handler.HandleAsync(command, stoppingToken);
        Console.WriteLine(result);
        Console.WriteLine("");
        Console.WriteLine("LIST REGISTERED");
        command = new TCPMessageBody<string>("TOM", "LIST REGISTERED", "");
        result = await _handler.HandleAsync(command, stoppingToken);
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
        result = await _handler.HandleAsync(command, stoppingToken);
        Console.WriteLine(result);
        Console.WriteLine("");
        Console.WriteLine("LIST REGISTERED");
        command = new TCPMessageBody<string>("TOM", "LIST REGISTERED", "");
        result = await _handler.HandleAsync(command, stoppingToken);
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
        result = await _handler.HandleAsync(command, stoppingToken);
        Console.WriteLine(result);

        Console.WriteLine("");
        Console.WriteLine("LIST REGISTERED");
        command = new TCPMessageBody<string>("TOM", "LIST REGISTERED", "");
        result = await _handler.HandleAsync(command, stoppingToken);
        Console.WriteLine(result);
        Console.WriteLine("");
        Console.WriteLine("UNREGISTER");
        command = new TCPMessageBody<string>("TOM", "UNREGISTER", function);
        result = await _handler.HandleAsync(command, stoppingToken);
        Console.WriteLine(result);
        Console.WriteLine("");
        Console.WriteLine("UNREGISTER SOMETHNG ALREADY UNREGISTERED");
        command = new TCPMessageBody<string>("TOM", "UNREGISTER", function);
        result = await _handler.HandleAsync(command, stoppingToken);
        Console.WriteLine(result);
        Console.WriteLine("");
        Console.WriteLine("LIST REGISTERED");
        command = new TCPMessageBody<string>("TOM", "LIST REGISTERED", "");
        result = await _handler.HandleAsync(command, stoppingToken);
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
        result = await _handler.HandleAsync(command, stoppingToken);
        Console.WriteLine(result);
        Console.WriteLine("");
        Console.WriteLine("OPEN COMM ");
        command = new TCPMessageBody<string>("TOM", "OPEN", function);
        result = await _handler.HandleAsync(command, stoppingToken);
        Console.WriteLine(result);
        Console.WriteLine("");
        Console.WriteLine("OPEN COMM  TWICE");
        command = new TCPMessageBody<string>("TOM", "OPEN", function);
        result = await _handler.HandleAsync(command, stoppingToken);
        Console.WriteLine(result);
        Console.WriteLine("");
        Console.WriteLine("CLOSE COMM ");
        command = new TCPMessageBody<string>("TOM", "CLOSE", function);
        result = await _handler.HandleAsync(command, stoppingToken);
        Console.WriteLine(result);
        Console.WriteLine("");
        Console.WriteLine("CLOSE COMM  TWICE");
        command = new TCPMessageBody<string>("TOM", "CLOSE", function);
        result = await _handler.HandleAsync(command, stoppingToken);
        Console.WriteLine(result);
        Console.WriteLine("");
        Console.WriteLine("WRITE TEXT TO CLOSED PORT");
        command = new TCPMessageBody<string>(function, "WRITE TEXT", TOM.TurnOnAllSystemsCommand);
        result = await _handler.HandleAsync(command, stoppingToken);
        Console.WriteLine(result);
        Console.WriteLine("");
        Console.WriteLine("OPEN COMM ");
        command = new TCPMessageBody<string>("TOM", "OPEN", function);
        result = await _handler.HandleAsync(command, stoppingToken);
        Console.WriteLine(result);
        Console.WriteLine("");
        //Console.WriteLine("WRITE TEXT TURN TOM OFF");
        //command = new TCPMessageBody<string>(function, "WRITE TEXT", TOM.TurnOffAllSystemsCommand);
        //result = await _handler.HandleAsync(command, stoppingToken);
        //Console.WriteLine(result);
        Console.WriteLine("");
        Console.WriteLine("WRITE TEXT TURN TOM ON");
        command = new TCPMessageBody<string>(function, "WRITE TEXT", TOM.TurnOnAllSystemsCommand);
        result = await _handler.HandleAsync(command, stoppingToken);
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
        result = await _handler.HandleAsync(command, stoppingToken);
        Console.WriteLine(result);
        Console.WriteLine("");
        Console.WriteLine("OPEN TOM OUPUT ");
        command = new TCPMessageBody<string>("TOM", "OPEN", function2);
        result = await _handler.HandleAsync(command, stoppingToken);
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
        result = await _handler.HandleAsync(command, stoppingToken);
        Console.WriteLine(result);
        Console.WriteLine("");
        Console.WriteLine("OPEN FLIR ");
        command = new TCPMessageBody<string>("TOM", "OPEN", function3);
        result = await _handler.HandleAsync(command, stoppingToken);
        Console.WriteLine(result);
        Console.WriteLine("");
        Console.WriteLine("WRITE FLIR RAINBOW");
        command = new TCPMessageBody<string>(function3, "WRITE TEXT", FLIR.LUTtoRAINBOW);
        result = await _handler.HandleAsync(command, stoppingToken);
        Console.WriteLine(result);
        Console.WriteLine("");


        //TEST  TEST  TEST  TEST  TEST  TEST  TEST  TEST  TEST  TEST  TEST  TEST  TEST  TEST  TEST  TEST  TEST  

    }
}
