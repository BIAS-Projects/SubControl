using System.IO.Ports;

namespace SubConsole.Models;



public sealed record SerialPortSettings(
    int BaudRate = 9600,
    int DataBits = 8,
    Parity Parity = Parity.None,
    StopBits StopBits = StopBits.One,
    Handshake Handshake = Handshake.None,
    bool KeepAliveEnabled = false,
    string KeepAlivePayload = ";",
    bool DtrEnable = false,
    bool RtsEnable = false,
    int KeepAliveIntervalMs = 1_000)
{
    public static SerialPortSettings Default { get; } = new();

    public string? Validate()
    {
        if (BaudRate <= 0) return "BaudRate must be positive";
        if (DataBits is < 5 or > 8) return "DataBits must be 5–8";
        if (StopBits == StopBits.None) return "StopBits cannot be None";
        if (KeepAliveEnabled && string.IsNullOrEmpty(KeepAlivePayload))
            return "KeepAlivePayload required when KeepAliveEnabled";
        if (KeepAliveEnabled && KeepAliveIntervalMs <= 0)
            return "KeepAliveIntervalMs must be positive";
        return null;
    }
}