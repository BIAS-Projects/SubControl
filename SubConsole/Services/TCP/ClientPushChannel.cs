using SubConsole.Models;
using System.Threading.Channels;

namespace SubConsole.Services.TCP;

/// <summary>
/// Wraps the outbound push channel for a single connected TCP client.
///
/// The serial fan-in pump holds a reference to every active
/// <see cref="ClientPushChannel"/> and writes device messages into it.
/// The TCP send loop drains it and forwards frames to the socket.
/// </summary>
public sealed class ClientPushChannel : IDisposable
{
    private readonly Channel<PushFrame> _channel =
        Channel.CreateBounded<PushFrame>(new BoundedChannelOptions(512)
        { FullMode = BoundedChannelFullMode.DropOldest });

    public ChannelWriter<PushFrame> Writer => _channel.Writer;
    public ChannelReader<PushFrame> Reader => _channel.Reader;

    public void Complete() => _channel.Writer.TryComplete();

    public void Dispose() => Complete();
}

/// <summary>
/// A message pushed from the server to the client carrying data received
/// from a serial device. The <see cref="FunctionName"/> identifies which
/// logical device produced the message — the client uses this to pair
/// responses with the commands it sent.
///
/// Wire format:
///   PUSH|{FunctionName}|{Text}<EOM>
///
/// For binary workers, Text is a base64-encoded payload.
/// </summary>
public sealed class PushFrame
{
    /// <summary>
    /// Logical function name of the port that produced this message
    /// (e.g. "TOM_CONTROLLER", "FLIR_CAMERA", "GPS_STREAM").
    /// The client uses this to route the message to the right handler.
    /// </summary>
    public required string FunctionName { get; init; }

    /// <summary>Decoded text payload (text-mode workers).</summary>
    public string? Text { get; init; }

    /// <summary>Raw byte payload (binary workers). Null for text workers.</summary>
    public byte[]? Payload { get; init; }

    public DateTimeOffset Timestamp { get; } = DateTimeOffset.UtcNow;

    /// <summary>
    /// Serialise to the wire format:
    ///   PUSH|{FunctionName}|{text or base64}<EOM>
    /// </summary>
    public string ToWireFrame(string eom)
    {
        var body = Text
            ?? (Payload is not null ? Convert.ToBase64String(Payload) : string.Empty);

        return $"PUSH|{FunctionName}|{body}{eom}";
    }
}
