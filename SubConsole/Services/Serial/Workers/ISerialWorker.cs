using SubConsole.Models;
using System.Threading.Channels;

namespace SubConsole.Services.Serial;

/// <summary>
/// Lifecycle contract for all serial workers.
/// Workers are passive — they push received data into an outbound channel
/// and pull data to send from an inbound channel.
/// </summary>
public interface ISerialWorker : IAsyncDisposable
{
    string PortPath { get; }
    bool IsOpen { get; }

    Task StartAsync(CancellationToken appToken);
    Task StopAsync();

    /// <summary>Enqueue a raw-byte write. Fire-and-forget from the caller's side.</summary>
    ValueTask<bool> WriteAsync(byte[] data, CancellationToken token = default);

    /// <summary>Convenience overload for text workers.</summary>
    ValueTask<bool> WriteTextAsync(string text, CancellationToken token = default);

    /// <summary>
    /// Messages received from the physical port.
    /// Consumers read from this channel; the worker is the sole writer.
    /// </summary>
    ChannelReader<SerialMessage> ReceivedMessages { get; }
}
