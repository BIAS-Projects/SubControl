using Microsoft.Extensions.Logging;
using SubConsole.Models;
using System.Management;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;

namespace SubConsole.Services.Serial;

/// <summary>
/// Enumerates USB-to-serial devices on the current OS and returns
/// a <see cref="DeviceIdentifier"/> for each one, together with the
/// OS port path (e.g. "COM3" or "/dev/ttyUSB0").
/// </summary>
public interface IUsbDeviceEnumerator
{
    Task<IReadOnlyList<(DeviceIdentifier Identifier, string PortPath)>> EnumerateAsync(
        CancellationToken token = default);
}

// ═════════════════════════════════════════════════════════════════════════════
// Windows — WMI Win32_PnPEntity + Win32_SerialPort
// ═════════════════════════════════════════════════════════════════════════════

public sealed class WindowsUsbDeviceEnumerator : IUsbDeviceEnumerator
{
    private readonly ILogger<WindowsUsbDeviceEnumerator> _logger;

    public WindowsUsbDeviceEnumerator(ILogger<WindowsUsbDeviceEnumerator> logger)
        => _logger = logger;

    public Task<IReadOnlyList<(DeviceIdentifier, string)>> EnumerateAsync(
        CancellationToken token = default)
    {
        return Task.Run<IReadOnlyList<(DeviceIdentifier, string)>>(() =>
        {
            var results = new List<(DeviceIdentifier, string)>();

            try
            {
                // Query USB serial converters via PnP — filters to USB\VID_ devices
                // that expose a COM port.
                using var searcher = new ManagementObjectSearcher(
                    @"SELECT * FROM Win32_PnPEntity WHERE ClassGuid = '{4d36e978-e325-11ce-bfc1-08002be10318}'");

                foreach (ManagementObject obj in searcher.Get())
                {
                    token.ThrowIfCancellationRequested();

                    var pnpId = obj["PNPDeviceID"]?.ToString() ?? string.Empty;
                    var caption = obj["Caption"]?.ToString() ?? string.Empty;
                    var mfr = obj["Manufacturer"]?.ToString() ?? string.Empty;

                    // Extract VID / PID / SN from the PNP device ID
                    // e.g. USB\VID_0403&PID_6001\A9Z3KF01
                    var vid = ExtractSegment(pnpId, @"VID_([0-9A-Fa-f]{4})");
                    var pid = ExtractSegment(pnpId, @"PID_([0-9A-Fa-f]{4})");
                    var sn = ExtractSerialNumber(pnpId);

                    if (string.IsNullOrEmpty(vid))
                        continue;

                    // Extract the COM port name from the caption, e.g. "(COM3)"
                    var port = Regex.Match(caption, @"\(COM\d+\)").Value
                                   .Trim('(', ')');

                    if (string.IsNullOrEmpty(port))
                        continue;

                    var identifier = new DeviceIdentifier(vid, pid, sn, mfr, caption);
                    results.Add((identifier, port));

                    _logger.LogDebug(
                        "Found USB serial: {Key} on {Port}", identifier.Key, port);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError(ex, "WMI enumeration failed");
            }

            return results;
        }, token);
    }

    private static string ExtractSegment(string source, string pattern)
    {
        var m = Regex.Match(source, pattern, RegexOptions.IgnoreCase);
        return m.Success ? m.Groups[1].Value.ToUpperInvariant() : string.Empty;
    }

    private static string ExtractSerialNumber(string pnpId)
    {
        // PNP ID format: BUS\VID_xxxx&PID_xxxx\<serial>
        var parts = pnpId.Split('\\');
        if (parts.Length < 3) return string.Empty;

        // The last segment is the serial number / instance ID.
        // Skip Windows-generated composite suffixes like "&0000"
        var last = parts[^1];
        return last.Contains('&') ? string.Empty : last.ToUpperInvariant();
    }
}

// ═════════════════════════════════════════════════════════════════════════════
// Linux — /sys/bus/usb/devices + /dev/serial/by-id symlinks
// ═════════════════════════════════════════════════════════════════════════════

public sealed class LinuxUsbDeviceEnumerator : IUsbDeviceEnumerator
{
    private static readonly string SysUsbBase = "/sys/bus/usb/devices";
    private static readonly string ByIdDir = "/dev/serial/by-id";
    private static readonly string ByPathDir = "/dev/serial/by-path";

    private readonly ILogger<LinuxUsbDeviceEnumerator> _logger;

    public LinuxUsbDeviceEnumerator(ILogger<LinuxUsbDeviceEnumerator> logger)
        => _logger = logger;

    public Task<IReadOnlyList<(DeviceIdentifier, string)>> EnumerateAsync(
        CancellationToken token = default)
    {
        return Task.Run<IReadOnlyList<(DeviceIdentifier, string)>>(() =>
        {
            var results = new List<(DeviceIdentifier, string)>();

            // Strategy 1: /dev/serial/by-id — symlinks with human-readable names
            // that embed VID, PID, and serial number when available.
            if (Directory.Exists(ByIdDir))
                results.AddRange(EnumerateByIdDir(token));

            // Strategy 2: Walk /sys/bus/usb/devices looking for tty children.
            // Catches devices that have no by-id symlink (no serial number).
            results.AddRange(EnumerateSysUsbDevices(token));

            // Deduplicate by port path — by-id is preferred because it carries the SN.
            return results
                .GroupBy(r => r.Item2, StringComparer.OrdinalIgnoreCase)
                .Select(g => g.OrderByDescending(r => r.Item1.SerialNumber.Length).First())
                .ToList()
                .AsReadOnly();
        }, token);
    }

    // ── /dev/serial/by-id ────────────────────────────────────────────────────

    private IEnumerable<(DeviceIdentifier, string)> EnumerateByIdDir(CancellationToken token)
    {
        // Symlink name format (udev):
        //   usb-<Manufacturer>_<Product>_<SerialNumber>-<port>
        // e.g. usb-FTDI_FT232R_USB_UART_A6003F2H-if00-port0 → /dev/ttyUSB0
        foreach (var symlink in Directory.GetFiles(ByIdDir))
        {
            token.ThrowIfCancellationRequested();

            var portPath = ResolveSymlink(symlink);
            if (portPath is null) continue;

            var name = Path.GetFileName(symlink);

            // Parse  usb-<mfr>_<product>_<sn>-if00-port0
            var parts = name.Split('-', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 2) continue;

            // Extract VID / PID from sysfs using the resolved tty name
            var (vid, pid) = ReadVidPidFromSysFs(portPath);
            var sn = ExtractSnFromByIdName(name);
            var mfr = parts.Length >= 2 ? parts[1] : "Unknown";
            var desc = name;

            var identifier = new DeviceIdentifier(vid, pid, sn, mfr, desc);
            _logger.LogDebug("by-id: {Key} → {Port}", identifier.Key, portPath);

            yield return (identifier, portPath);
        }
    }

    private static string? ResolveSymlink(string path)
    {
        try
        {
            var real = new FileInfo(path).ResolveLinkTarget(returnFinalTarget: true);
            return real?.FullName;
        }
        catch { return null; }
    }

    private static string ExtractSnFromByIdName(string name)
    {
        // usb-<mfr>_<product>_<SN>-<suffix>
        // SN is the last _-delimited token before the first '-if' or '-port'
        var withoutPrefix = name.StartsWith("usb-") ? name[4..] : name;
        var ifIdx = withoutPrefix.IndexOf("-if", StringComparison.Ordinal);
        var portIdx = withoutPrefix.IndexOf("-port", StringComparison.Ordinal);
        var end = new[] { ifIdx, portIdx }.Where(i => i >= 0).DefaultIfEmpty(-1).Min();

        var core = end >= 0 ? withoutPrefix[..end] : withoutPrefix;
        var underscored = core.Split('_');
        return underscored.Length >= 3 ? underscored[^1].ToUpperInvariant() : string.Empty;
    }

    // ── /sys/bus/usb/devices ─────────────────────────────────────────────────

    private IEnumerable<(DeviceIdentifier, string)> EnumerateSysUsbDevices(CancellationToken token)
    {
        if (!Directory.Exists(SysUsbBase)) yield break;

        foreach (var devDir in Directory.GetDirectories(SysUsbBase))
        {
            token.ThrowIfCancellationRequested();

            var vid = ReadSysFile(devDir, "idVendor");
            var pid = ReadSysFile(devDir, "idProduct");
            var sn = ReadSysFile(devDir, "serial");
            var mfr = ReadSysFile(devDir, "manufacturer");
            var desc = ReadSysFile(devDir, "product");

            if (string.IsNullOrEmpty(vid)) continue;

            // Look for tty children: devDir/<intf>/<intf>.1/tty/ttyUSB0
            var ttyPorts = FindTtyDescendants(devDir);
            foreach (var tty in ttyPorts)
            {
                var identifier = new DeviceIdentifier(
                    vid.ToUpperInvariant(),
                    pid.ToUpperInvariant(),
                    sn.ToUpperInvariant(),
                    mfr,
                    desc);

                _logger.LogDebug("sysfs: {Key} → {Port}", identifier.Key, tty);
                yield return (identifier, tty);
            }
        }
    }

    private static IEnumerable<string> FindTtyDescendants(string baseDir)
    {
        // Breadth-first search for directories named "tty".
        // Cannot yield inside a try/catch (CS1626), so results are collected
        // into a list and yielded after the loop completes.
        var queue = new Queue<string>();
        var results = new List<string>();
        queue.Enqueue(baseDir);

        while (queue.Count > 0)
        {
            var dir = queue.Dequeue();
            var toEnqueue = new List<string>();

            try
            {
                foreach (var sub in Directory.GetDirectories(dir))
                {
                    if (Path.GetFileName(sub) == "tty")
                    {
                        foreach (var ttyEntry in Directory.GetDirectories(sub))
                            results.Add(Path.Combine("/dev", Path.GetFileName(ttyEntry)));

                        continue; // don't recurse into tty/
                    }

                    toEnqueue.Add(sub);
                }
            }
            catch { /* permission denied on some sysfs paths — skip silently */ }

            foreach (var sub in toEnqueue)
                queue.Enqueue(sub);
        }

        foreach (var path in results)
            yield return path;
    }

    private static (string vid, string pid) ReadVidPidFromSysFs(string devPath)
    {
        // e.g. /dev/ttyUSB0 → /sys/bus/usb-serial/drivers/ftdi_sio/ttyUSB0/../../..
        // Simpler: walk /sys/bus/usb/devices looking for matching tty
        var devName = Path.GetFileName(devPath);

        try
        {
            foreach (var dir in Directory.GetDirectories(SysUsbBase))
            {
                var tty = FindTtyDescendants(dir)
                    .Any(t => Path.GetFileName(t) == devName);

                if (!tty) continue;

                return (ReadSysFile(dir, "idVendor").ToUpperInvariant(),
                        ReadSysFile(dir, "idProduct").ToUpperInvariant());
            }
        }
        catch { }

        return (string.Empty, string.Empty);
    }

    private static string ReadSysFile(string dir, string file)
    {
        try
        {
            var path = Path.Combine(dir, file);
            return File.Exists(path) ? File.ReadAllText(path).Trim() : string.Empty;
        }
        catch { return string.Empty; }
    }
}

// ═════════════════════════════════════════════════════════════════════════════
// Factory — picks the right enumerator for the current OS
// ═════════════════════════════════════════════════════════════════════════════

public static class UsbDeviceEnumeratorFactory
{
    public static IUsbDeviceEnumerator Create(ILoggerFactory loggerFactory)
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            return new WindowsUsbDeviceEnumerator(
                loggerFactory.CreateLogger<WindowsUsbDeviceEnumerator>());

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            return new LinuxUsbDeviceEnumerator(
                loggerFactory.CreateLogger<LinuxUsbDeviceEnumerator>());

        throw new PlatformNotSupportedException("Only Windows and Linux are supported.");
    }
}

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