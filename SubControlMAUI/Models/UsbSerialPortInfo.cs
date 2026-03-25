using System.Text.Json;

namespace SubConsole.Models;

public record UsbSerialPortInfo
{
    public string PortName { get; init; } = "";
    public string VendorId { get; init; } = "";
    public string ProductId { get; init; } = "";
    public string SerialNumber { get; init; } = "";
    public string Description { get; init; } = "";
    public string DeviceId { get; init; } = "";

    public override string ToString() => JsonSerializer.Serialize(this);
}