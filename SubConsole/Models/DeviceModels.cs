namespace SubConsole.Models;

/// <summary>
/// Stable, hardware-derived identity for a USB device.
/// Built from VID + PID + SerialNumber (or Manufacturer fallback).
/// </summary>
public sealed record DeviceIdentifier(
    string VendorId,
    string ProductId,
    string SerialNumber,
    string Manufacturer,
    string Description)
{
    /// <summary>
    /// Canonical key used in the registry: "VID:PID:SN" (all upper-case).
    /// Falls back to Manufacturer when SN is absent so lab kit without
    /// serial numbers still gets a stable key.
    /// </summary>
    public string Key =>
        $"{VendorId.ToUpperInvariant()}:{ProductId.ToUpperInvariant()}:" +
        $"{(string.IsNullOrWhiteSpace(SerialNumber) ? Manufacturer.ToUpperInvariant() : SerialNumber.ToUpperInvariant())}";

    public override string ToString() =>
        $"[{Key}] {Description}";
}

/// <summary>
/// Associates a <see cref="DeviceIdentifier"/> with one or more logical
/// function names (e.g. "FLIR_CAMERA", "TOM_CONTROLLER").
/// The mapping from function name → current OS port path lives in
/// <see cref="IDeviceRegistry"/>.
/// </summary>
public sealed class DeviceRegistration
{
    public DeviceIdentifier Identifier { get; }
    public IReadOnlyList<string> FunctionNames { get; }

    /// <summary>Current OS port path (e.g. "COM3" or "/dev/ttyUSB0").</summary>
    public string? CurrentPortPath { get; internal set; }

    public DeviceRegistration(DeviceIdentifier identifier, IEnumerable<string> functionNames)
    {
        Identifier = identifier;
        FunctionNames = functionNames.ToList().AsReadOnly();
    }
}

/// <summary>
/// Returned to callers on every read from a serial port.
/// Carries the raw data plus enough context to route the message.
/// </summary>
public sealed class SerialMessage
{
    /// <summary>Logical function name resolved via the device registry.</summary>
    public required string FunctionName { get; init; }

    /// <summary>OS port path this message arrived on.</summary>
    public required string PortPath { get; init; }

    /// <summary>Raw byte payload — never null, may be empty.</summary>
    public required byte[] Payload { get; init; }

    /// <summary>
    /// Decoded text payload, populated for text-mode workers.
    /// Null for binary-mode workers.
    /// </summary>
    public string? Text { get; init; }

    public DateTimeOffset Timestamp { get; } = DateTimeOffset.UtcNow;
}
