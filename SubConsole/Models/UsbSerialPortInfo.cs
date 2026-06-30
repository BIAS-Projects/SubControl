namespace SubConsole.Models;

public record UsbSerialPortInfo
{
    public string PortName { get; init; } = "";
    public string VendorId { get; init; } = "";
    public string ProductId { get; init; } = "";
    public string SerialNumber { get; init; } = "";
    public string Description { get; init; } = "";
    public string DeviceId { get; init; } = "";

    /// <summary>True for fixed, non-USB devices (e.g. onboard UART) that are
    /// never seen by USB enumeration and never hot-plug reassigned.</summary>
    public bool IsStatic { get; init; } = false;

    public string Key =>
        string.IsNullOrWhiteSpace(SerialNumber)
            ? $"{VendorId}:{ProductId}:{DeviceId}".ToUpperInvariant()
            : $"{VendorId}:{ProductId}:{DeviceId}:{SerialNumber}".ToUpperInvariant();

    public static UsbSerialPortInfo CreateStatic(string deviceId, string portPath) => new()
    {
        VendorId = "STATIC",
        ProductId = "STATIC",
        DeviceId = deviceId.ToUpperInvariant(),
        PortName = portPath,
        Description = "Static onboard serial device",
        IsStatic = true
    };
}