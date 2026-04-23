using System.Security.Principal;
using static SubConsole.Models.UsbDeviceInfo;

/// <summary>
/// Stable, hardware-derived identity for a USB device.
/// Built from VID + PID + SerialNumber.
///
/// Key uniqueness rules (in priority order):
///   1. SerialNumber present  → VID:PID:SN          (always unique per port on FTDI chips)
///   2. SerialNumber absent   → VID:PID:PORT         (port path used as discriminator,
///                                                    e.g. devices with no programmed SN)
///
/// Manufacturer is never used in the key because multiple devices from the
/// same vendor share it and it would collapse them into one entry.
/// </summary>
public sealed record DeviceIdentifier(
    string VendorId,
    string ProductId,
    string SerialNumber,
    string Manufacturer,
    string Description)
{
    /// <summary>
    /// Canonical key: "VID:PID:SN" when a serial number is available,
    /// "VID:PID:PORT" otherwise. All segments are upper-case.
    /// Stored alongside a port path in <see cref="DeviceRegistration"/>
    /// so the port path is always available for the fallback.
    /// </summary>
    public string BuildKey(string portPath)
    {
        var discriminator = string.IsNullOrWhiteSpace(SerialNumber)
            ? portPath.ToUpperInvariant()
            : SerialNumber.ToUpperInvariant();

        return $"{VendorId.ToUpperInvariant()}:{ProductId.ToUpperInvariant()}:{discriminator}";
    }

    /// <summary>
    /// Convenience property — returns a key using an empty port path.
    /// Only use this when port path is not yet known (e.g. during parsing).
    /// Prefer <see cref="BuildKey(string)"/> wherever a port is available.
    /// </summary>
    public string Key => BuildKey(string.Empty);

    public override string ToString() =>
        $"[{Key}] {Description} ({Manufacturer})";
}

/// <summary>
/// Associates a <see cref="DeviceIdentifier"/> with one or more logical
/// function names (e.g. "FLIR_CAMERA", "TOM_CONTROLLER").
/// </summary>
public sealed class DeviceRegistration
{
    public DeviceIdentifier Identifier { get; }

    /// <summary>Stable registry key, built at registration time with the known port path.</summary>
    public string Key { get; }

    public string FunctionName { get; }

    public int BaudRate { get; set; }

    public SerialWorkerType SerialWorkerType { get;  }

    /// <summary>Current OS port path (e.g. "COM3" or "/dev/ttyUSB0").</summary>
    public string? CurrentPortPath { get; internal set; }

    public DeviceRegistration(
        DeviceIdentifier identifier, string functionName, int baudRate, SerialWorkerType serialWorker)
    {
        Identifier = identifier;
        FunctionName = functionName;
        SerialWorkerType = serialWorker;
        BaudRate = baudRate;
        //Key = identifier.BuildKey(portPath);
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
