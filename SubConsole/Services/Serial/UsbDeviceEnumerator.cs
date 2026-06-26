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

            _logger.LogInformation("Starting USB serial enumeration (Windows/WMI)");

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
                        "Discovered USB serial device {DeviceKey} on {PortPath}",
                        identifier.Key,
                        port);
                }
            }
            catch (OperationCanceledException)
            {
                _logger.LogDebug("USB serial enumeration cancelled (Windows/WMI)");
                throw;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError(ex, "WMI USB enumeration failed");
            }

            _logger.LogInformation(
                "Completed USB serial enumeration (Windows). Found {DeviceCount} device(s)",
                results.Count);

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
    private static readonly string SysTtyClass = "/sys/class/tty";

    private readonly ILogger<LinuxUsbDeviceEnumerator> _logger;

    public LinuxUsbDeviceEnumerator(ILogger<LinuxUsbDeviceEnumerator> logger)
        => _logger = logger;

    public Task<IReadOnlyList<(DeviceIdentifier, string)>> EnumerateAsync(
        CancellationToken token = default)
    {
        return Task.Run<IReadOnlyList<(DeviceIdentifier, string)>>(() =>
        {
            var results = new List<(DeviceIdentifier, string)>();

            _logger.LogInformation("=== Linux USB enumeration starting ===");

            // ── Sanity-check key paths ────────────────────────────────────────

            _logger.LogInformation(
                "/sys/bus/usb/devices exists: {Exists}",
                Directory.Exists(SysUsbBase));

            _logger.LogInformation(
                "/sys/class/tty exists: {Exists}",
                Directory.Exists(SysTtyClass));

            _logger.LogInformation(
                "/dev/serial/by-id exists: {Exists}",
                Directory.Exists(ByIdDir));

            // ── Dump all ttyACM / ttyUSB entries in /sys/class/tty ────────────

            try
            {
                var ttyEntries = Directory.GetDirectories(SysTtyClass)
                    .Select(Path.GetFileName)
                    .Where(n => n != null &&
                                (n.StartsWith("ttyUSB", StringComparison.Ordinal) ||
                                 n.StartsWith("ttyACM", StringComparison.Ordinal)))
                    .ToList();

                _logger.LogInformation(
                    "/sys/class/tty USB serial entries ({Count}): {Entries}",
                    ttyEntries.Count,
                    ttyEntries.Count == 0 ? "(none)" : string.Join(", ", ttyEntries));
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to enumerate /sys/class/tty");
            }

            // ── Dump top-level entries in /sys/bus/usb/devices ───────────────

            try
            {
                var usbDirs = Directory.GetDirectories(SysUsbBase)
                    .Select(Path.GetFileName)
                    .ToList();

                _logger.LogInformation(
                    "/sys/bus/usb/devices entries ({Count}): {Entries}",
                    usbDirs.Count,
                    string.Join(", ", usbDirs));
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to enumerate /sys/bus/usb/devices");
            }

            // ── by-id ─────────────────────────────────────────────────────────

            try
            {
                if (Directory.Exists(ByIdDir))
                {
                    _logger.LogInformation("Enumerating /dev/serial/by-id");
                    var byIdResults = EnumerateByIdDir(token).ToList();
                    _logger.LogInformation(
                        "by-id produced {Count} result(s)", byIdResults.Count);
                    results.AddRange(byIdResults);
                }
                else
                {
                    _logger.LogInformation(
                        "/dev/serial/by-id not present — skipping");
                }
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                _logger.LogError(ex, "by-id enumeration failed");
            }

            // ── sysfs ─────────────────────────────────────────────────────────

            try
            {
                _logger.LogInformation("Enumerating via /sys/bus/usb/devices");
                var sysResults = EnumerateSysUsbDevices(token).ToList();
                _logger.LogInformation(
                    "sysfs produced {Count} result(s)", sysResults.Count);
                results.AddRange(sysResults);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                _logger.LogError(ex, "sysfs enumeration failed");
            }

            // ── Deduplicate ───────────────────────────────────────────────────

            var deduped = results
                .GroupBy(r => r.Item2, StringComparer.OrdinalIgnoreCase)
                .Select(g => g.OrderByDescending(r => r.Item1.SerialNumber.Length).First())
                .ToList()
                .AsReadOnly();

            _logger.LogInformation(
                "=== Linux USB enumeration complete. Returning {Count} device(s) ===",
                deduped.Count);

            return deduped;

        }, token);
    }

    // ── /dev/serial/by-id ────────────────────────────────────────────────────

    private IEnumerable<(DeviceIdentifier, string)> EnumerateByIdDir(CancellationToken token)
    {
        foreach (var symlink in Directory.GetFiles(ByIdDir))
        {
            token.ThrowIfCancellationRequested();

            var portPath = ResolveSymlink(symlink);
            _logger.LogDebug(
                "by-id: {Symlink} → {PortPath}",
                symlink, portPath ?? "(unresolvable)");

            if (portPath is null) continue;

            var name = Path.GetFileName(symlink);
            var parts = name.Split('-', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 2)
            {
                _logger.LogDebug("by-id: skipping {Name} — too few '-' segments", name);
                continue;
            }

            var (vid, pid) = ReadVidPidFromSysFs(portPath);
            var sn = ExtractSnFromByIdName(name);
            var mfr = parts.Length >= 2 ? parts[1] : "Unknown";

            var identifier = new DeviceIdentifier(vid, pid, sn, mfr, name);
            _logger.LogInformation(
                "by-id: discovered {Key} on {Port}", identifier.Key, portPath);

            yield return (identifier, portPath);
        }
    }

    // ── /sys/bus/usb/devices ─────────────────────────────────────────────────

    private IEnumerable<(DeviceIdentifier, string)> EnumerateSysUsbDevices(
        CancellationToken token)
    {
        if (!Directory.Exists(SysUsbBase))
        {
            _logger.LogWarning("SysUsbBase does not exist: {Path}", SysUsbBase);
            yield break;
        }

        string[] deviceDirs;
        try
        {
            deviceDirs = Directory.GetDirectories(SysUsbBase);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Cannot list {SysUsbBase}", SysUsbBase);
            yield break;
        }

        _logger.LogDebug(
            "sysfs: found {Count} top-level entries", deviceDirs.Length);

        foreach (var devDir in deviceDirs)
        {
            token.ThrowIfCancellationRequested();

            var vid = ReadSysFile(devDir, "idVendor");
            var pid = ReadSysFile(devDir, "idProduct");

            if (string.IsNullOrEmpty(vid))
            {
                _logger.LogDebug(
                    "sysfs: {Dir} has no idVendor — skipping", devDir);
                continue;
            }

            var sn = ReadSysFile(devDir, "serial");
            var mfr = ReadSysFile(devDir, "manufacturer");
            var desc = ReadSysFile(devDir, "product");

            _logger.LogInformation(
                "sysfs: device at {Dir} — VID={Vid} PID={Pid} SN={Sn} MFR={Mfr} DESC={Desc}",
                devDir, vid, pid, sn, mfr, desc);

            var ttyPorts = FindTtyDescendants(devDir).ToList();

            _logger.LogInformation(
                "sysfs: FindTtyDescendants({Dir}) → {Count} port(s): [{Ports}]",
                devDir,
                ttyPorts.Count,
                string.Join(", ", ttyPorts));

            foreach (var tty in ttyPorts)
            {
                var identifier = new DeviceIdentifier(
                    vid.ToUpperInvariant(),
                    pid.ToUpperInvariant(),
                    sn.ToUpperInvariant(),
                    mfr,
                    desc);

                _logger.LogInformation(
                    "sysfs: yielding {Key} on {Port}", identifier.Key, tty);

                yield return (identifier, tty);
            }
        }
    }

    // ── FindTtyDescendants — uses /sys/class/tty to avoid BFS/colon issues ───

    private List<string> FindTtyDescendants(string baseDir)
    {
        var results = new List<string>();

        string baseDirReal;
        try
        {
            baseDirReal = ResolvePath(baseDir);
            _logger.LogDebug(
                "FindTtyDescendants: baseDir={BaseDir} resolved={Real}",
                baseDir, baseDirReal);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "FindTtyDescendants: failed to resolve baseDir {BaseDir}", baseDir);
            return results;
        }

        string[] ttyDirs;
        try
        {
            ttyDirs = Directory.GetDirectories(SysTtyClass);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "FindTtyDescendants: cannot list {SysTtyClass}", SysTtyClass);
            return results;
        }

        foreach (var ttyDir in ttyDirs)
        {
            var name = Path.GetFileName(ttyDir);

            if (!name.StartsWith("ttyUSB", StringComparison.Ordinal) &&
                !name.StartsWith("ttyACM", StringComparison.Ordinal))
                continue;

            try
            {
                var deviceLink = Path.Combine(ttyDir, "device");
                var resolved = ResolvePath(deviceLink);

                _logger.LogDebug(
                    "FindTtyDescendants: {Name} device link → {Resolved}",
                    name, resolved ?? "(null)");

                if (resolved is not null && resolved.StartsWith(
                        baseDirReal, StringComparison.Ordinal))
                {
                    _logger.LogInformation(
                        "FindTtyDescendants: {Name} matches {BaseDir} ✓", name, baseDir);
                    results.Add(Path.Combine("/dev", name));
                }
                else
                {
                    _logger.LogDebug(
                        "FindTtyDescendants: {Name} resolved to {Resolved} " +
                        "which does not start with {Base} — skipping",
                        name, resolved ?? "(null)", baseDirReal);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "FindTtyDescendants: error processing {Name}", name);
            }
        }

        return results;
    }

    // ── ReadVidPidFromSysFs ───────────────────────────────────────────────────

    private (string vid, string pid) ReadVidPidFromSysFs(string devPath)
    {
        var devName = Path.GetFileName(devPath);

        try
        {
            var ttyDeviceLink = Path.Combine(SysTtyClass, devName, "device");
            var interfaceDir = ResolvePath(ttyDeviceLink);

            _logger.LogDebug(
                "ReadVidPid: {DevName} device link → {InterfaceDir}",
                devName, interfaceDir ?? "(null)");

            if (interfaceDir is null) return (string.Empty, string.Empty);

            var deviceDir = Path.GetDirectoryName(interfaceDir);

            _logger.LogDebug(
                "ReadVidPid: deviceDir={DeviceDir}", deviceDir ?? "(null)");

            if (deviceDir is null) return (string.Empty, string.Empty);

            var vid = ReadSysFile(deviceDir, "idVendor").ToUpperInvariant();
            var pid = ReadSysFile(deviceDir, "idProduct").ToUpperInvariant();

            _logger.LogDebug(
                "ReadVidPid: {DevName} → VID={Vid} PID={Pid}", devName, vid, pid);

            return (vid, pid);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "ReadVidPid: failed for {DevName}", devName);
            return (string.Empty, string.Empty);
        }
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static string? ResolveSymlink(string path)
    {
        try
        {
            var real = new FileInfo(path).ResolveLinkTarget(returnFinalTarget: true);
            return real?.FullName;
        }
        catch { return null; }
    }

    private static string ResolvePath(string path)
    {
        try
        {
            return new FileInfo(path)
                .ResolveLinkTarget(returnFinalTarget: true)
                ?.FullName ?? Path.GetFullPath(path);
        }
        catch
        {
            return Path.GetFullPath(path);
        }
    }

    private static string ExtractSnFromByIdName(string name)
    {
        var withoutPrefix = name.StartsWith("usb-") ? name[4..] : name;
        var ifIdx = withoutPrefix.IndexOf("-if", StringComparison.Ordinal);
        var portIdx = withoutPrefix.IndexOf("-port", StringComparison.Ordinal);
        var end = new[] { ifIdx, portIdx }.Where(i => i >= 0).DefaultIfEmpty(-1).Min();
        var core = end >= 0 ? withoutPrefix[..end] : withoutPrefix;
        var parts = core.Split('_');
        return parts.Length >= 3 ? parts[^1].ToUpperInvariant() : string.Empty;
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
        var logger = loggerFactory.CreateLogger("UsbDeviceEnumeratorFactory");

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            logger.LogInformation("Using Windows USB device enumerator");
            return new WindowsUsbDeviceEnumerator(
                loggerFactory.CreateLogger<WindowsUsbDeviceEnumerator>());
        }
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            logger.LogInformation("Using Linux USB device enumerator");
            return new LinuxUsbDeviceEnumerator(
                loggerFactory.CreateLogger<LinuxUsbDeviceEnumerator>());
        }

        logger.LogError("Unsupported OS platform for USB enumeration");
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