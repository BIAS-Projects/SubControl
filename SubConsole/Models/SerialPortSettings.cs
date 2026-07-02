using System.IO.Ports;

namespace SubConsole.Models;

public sealed record SerialPortSettings(
    int DataBits = 8,
    Parity Parity = Parity.None,
    StopBits StopBits = StopBits.One,
    Handshake Handshake = Handshake.None,
    bool KeepAliveEnabled = false,
    string KeepAlivePayload = ";",
    int KeepAliveIntervalMs = 1_000)
{
    public static SerialPortSettings Default { get; } = new();
}