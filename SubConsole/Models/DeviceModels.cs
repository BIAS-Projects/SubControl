using SubConsole.Models;
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
//public sealed record DeviceIdentifier(
//    string VendorId,
//    string ProductId,
//    string SerialNumber,
//    string Manufacturer,
//    string Description)
//{
//    /// <summary>
//    /// Canonical key: "VID:PID:SN" when a serial number is available,
//    /// "VID:PID:PORT" otherwise. All segments are upper-case.
//    /// Stored alongside a port path in <see cref="DeviceRegistration"/>
//    /// so the port path is always available for the fallback.
//    /// </summary>
//    public string BuildKey(string portPath)
//    {
//        var discriminator = string.IsNullOrWhiteSpace(SerialNumber)
//            ? portPath.ToUpperInvariant()
//            : SerialNumber.ToUpperInvariant();

//        return $"{VendorId.ToUpperInvariant()}:{ProductId.ToUpperInvariant()}:{discriminator}";
//    }

//    /// <summary>
//    /// Convenience property — returns a key using an empty port path.
//    /// Only use this when port path is not yet known (e.g. during parsing).
//    /// Prefer <see cref="BuildKey(string)"/> wherever a port is available.
//    /// </summary>
//    public string Key => BuildKey(string.Empty);

//    public override string ToString() =>
//        $"[{Key}] {Description} ({Manufacturer})";
//}



