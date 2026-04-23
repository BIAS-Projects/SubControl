//using Gst;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using SubConsole.Helpers;
using SubConsole.Models;
using SubConsole.Services.Serial;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using static SQLite.SQLite3;

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

    public TcpHostService(
        ILogger<TcpHostService> logger,
        TcpSerialCommandHandler handler,
        SerialPushPump pushPump)
    {
        _logger   = logger;
        _handler  = handler;
        _pushPump = pushPump;
        _listener = new TcpListener(IPAddress.Any, 9000);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _listener.Start();
        _logger.LogInformation("TCP server started on port 9000");

        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {


                await UsbPortRegistry.Instance.RefreshAsync();


                for (int i = 1; i < 10; i++)
                {
                    if (UsbPortRegistry.Instance.TryGetPort($"COM{i}", out var info))
                       Console.WriteLine($"{info.Description} {info.ProductId} {info.PortName} {info.VendorId} {info.SerialNumber}");

 
                }

               var result = await _handler.HandleAsync(new TCPMessageBody<string>("", "LIST DEVICES", ""), stoppingToken);



                var client = await _listener.AcceptTcpClientAsync(stoppingToken);
                var id     = Guid.NewGuid();

                _logger.LogInformation(
                    "Client {Id} connected from {Endpoint}", id, client.Client.RemoteEndPoint);

                // Register with the push pump before starting loops so no
                // messages can be missed between connection and registration.
                var pushChannel = _pushPump.AddClient(id);
                var state       = new ClientState(id, client, pushChannel);
                _clients[id]    = state;

                _ = Task.Run(() => ReceiveLoop(state, stoppingToken), stoppingToken);
                _ = Task.Run(() => ProcessLoop(state, stoppingToken), stoppingToken);
                _ = Task.Run(() => SendLoop(state, stoppingToken),    stoppingToken);
            }
        }
        catch (OperationCanceledException) { }
        finally
        {
            _listener.Stop();
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

                    await state.Incoming.Writer.WriteAsync(new IncomingFrame(id, command), token);
                }

                sb.Clear();
                sb.Append(current);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Client {Id} receive error", state.Id);
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
            try
            {
                // ACK immediately so the client's timeout is never hit by handler latency
                await state.Outgoing.Writer.WriteAsync(
                    $"{TcpProtocol.ACK}{TcpProtocol.SEP}{frame.Id}{TcpProtocol.EOM}", token);

                var message = JsonSerializer.Deserialize<TCPMessageBody<string>>(frame.Command);

                if (message == null)
                {
                    throw new InvalidOperationException($"Invalid JSON message: {frame.Command}");
                }

              //  var result = await _handler.HandleAsync(frame.Command, token);
                var result = await _handler.HandleAsync(message, token);

                await state.Outgoing.Writer.WriteAsync(
                    $"{frame.Id}{TcpProtocol.SEP}{result}{TcpProtocol.EOM}", token);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Client {Id} process error for command '{Command}'",
                    state.Id, frame.Command);

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

                var bytes = Encoding.UTF8.GetBytes(message);
                await stream.WriteAsync(bytes, token);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Client {Id} send error", state.Id);
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

        _logger.LogInformation("Client {Id} disconnected", id);
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
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
}
