// Models/UsbSerialPortInfo.cs
namespace SubConsole.Models;

public record UsbSerialPortInfo
{
    public string PortName { get; init; } = "";
    public string VendorId { get; init; } = "";
    public string ProductId { get; init; } = "";
    public string SerialNumber { get; init; } = "";
    public string Description { get; init; } = "";
    public string DeviceId { get; init; } = "";


    public string Key =>
    //string.IsNullOrWhiteSpace(SerialNumber)
    //    ? $"{VendorId}:{ProductId}:{Description}:{DeviceId}".ToUpperInvariant()
    //    : $"{VendorId}:{ProductId}:{Description}:{DeviceId}:{SerialNumber}".ToUpperInvariant();
    string.IsNullOrWhiteSpace(SerialNumber)
            ? $"{VendorId}:{ProductId}:{DeviceId}".ToUpperInvariant()
            : $"{VendorId}:{ProductId}:{DeviceId}:{SerialNumber}".ToUpperInvariant();
}
