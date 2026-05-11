using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using SubConsole.Models;
using SubConsole.Services.TCP;
using System.Collections.Concurrent;

namespace SubConsole.Services.Serial;

/// <summary>
/// Background pump that continuously reads from the serial broadcast channel
/// and forwards every <see cref="SerialMessage"/> to all connected TCP clients
/// as a <see cref="PushFrame"/>.
///
/// The <see cref="PushFrame.FunctionName"/> tells the client which logical
/// device produced the message. The client is responsible for pairing
/// messages with the commands that triggered them.
///
/// One instance is shared for all clients. Per-client channels are registered
/// and deregistered by <see cref="TcpHostService"/> as clients connect and
/// disconnect.
/// </summary>
public sealed class SerialPushPump : BackgroundService
{
    private readonly ILogger<SerialPushPump> _logger;
    private readonly ISerialPortManagerService _manager;

    private readonly ConcurrentDictionary<Guid, ClientPushChannel> _clients = new();

    public SerialPushPump(
        ILogger<SerialPushPump> logger,
        ISerialPortManagerService manager)
    {
        _logger  = logger;
        _manager = manager;
    }

    // ── Client registration ───────────────────────────────────────────────────

    public ClientPushChannel AddClient(Guid clientId)
    {
        var channel = new ClientPushChannel();
        _clients[clientId] = channel;
        _logger.LogInformation(
            "Client {ClientId} registered to push pump (TotalClients={Count})",
            clientId,
            _clients.Count);
        return channel;
    }

    public void RemoveClient(Guid clientId)
    {
        if (_clients.TryRemove(clientId, out var ch))
        {
            ch.Complete();
            ch.Dispose();
            _logger.LogInformation(
                "Client {ClientId} removed from push pump (TotalClients={Count})",
                clientId,
                _clients.Count);
        }
        else
        {
            _logger.LogDebug(
                "Attempted to remove unknown client {ClientId}",
                clientId);
        }
    }

    // ── Pump loop ─────────────────────────────────────────────────────────────

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("SerialPushPump started");

        // Empty function-name set = subscribe to all ports
        var reader = _manager.GetMessageReader(Enumerable.Empty<string>());

        try
        {
            await foreach (var message in reader.ReadAllAsync(stoppingToken))
            {


                _logger.LogDebug("PushPump received: {Function} {Text}",
    message.FunctionName, message.Text);

                _logger.LogDebug(
                    "Received serial message from {FunctionName}",
                    message.FunctionName);

                var frame = new PushFrame
                {
                    FunctionName = message.FunctionName,
                    Text         = message.Text,
                    Payload      = message.Payload
                };

                foreach (var (id, client) in _clients)
                {
                    if (!client.Writer.TryWrite(frame))
                    {
                        _logger.LogWarning(
                            "Push channel full for client {ClientId} — frame dropped (Function={FunctionName})",
                            id,
                            frame.FunctionName);
                    }
                }
            }
        }
        catch (OperationCanceledException) 
        {
            _logger.LogDebug("SerialPushPump cancellation requested");
        }

        _logger.LogInformation("SerialPushPump stopped");
    }
}
