using Microsoft.Extensions.Logging;
using Serilog.Core;
using SubConsole.Models;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using static SQLite.SQLite3;

#if WINDOWS
using Microsoft.Win32;
#endif

namespace SubConsole.Services.Helpers;

public static class UsbSerialPortMapper
{
    private static readonly SemaphoreSlim _lock = new(1, 1);

    private static ILogger? _logger;

    public static void ConfigureLogger(ILogger logger)
    {
        _logger = logger;
    }

    // ---------------- PUBLIC API ----------------




    //    // On startup or hardware change notification:
    //    await UsbPortRegistry.Instance.RefreshAsync();

    //// Read from any thread:
    //if (UsbPortRegistry.Instance.TryGetPort("COM3", out var info))
    //    Console.WriteLine(info.Description);

    //// React to plug/unplug:
    //UsbPortRegistry.Instance.PortChanged += (_, e) =>
    //    logger.LogInformation("{Kind}: {Port}", e.Kind, e.Port.PortName);

    public static async Task<IReadOnlyList<UsbSerialPortInfo>> GetUsbSerialPortsAsync(CancellationToken token)
    {
        await _lock.WaitAsync();
        try
        {


            return await Task.Run(() =>
            {
                if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                {
                    _logger?.LogInformation("Starting Windows USB serial port scan");
                    return GetWindowsUsbSerialPorts(token);

                }

                if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
                {
                    _logger?.LogInformation("Starting Linux USB serial port scan");
                    return GetLinuxUsbSerialPorts(token);
                }

                _logger?.LogWarning("No ports foud during port scan");
                return Array.Empty<UsbSerialPortInfo>();
            });
        }
        finally
        {
            _lock.Release();
        }
    }




    public static async Task<string> GetUsbSerialPortsAsJsonAsync(CancellationToken token)
    {

        var ports = await GetUsbSerialPortsAsync(token);
        return JsonSerializer.Serialize(ports, new JsonSerializerOptions
        {
            WriteIndented = true
        });
    }

    // ---------------- WINDOWS ----------------

    private static IReadOnlyList<UsbSerialPortInfo> GetWindowsUsbSerialPorts(CancellationToken token)
    {
        var results = new List<UsbSerialPortInfo>();

        try
        {
            var hklm = Microsoft.Win32.Registry.LocalMachine;
            var usbEnumKey = hklm.OpenSubKey(@"SYSTEM\CurrentControlSet\Enum\USB");

            if (usbEnumKey == null)
            {
                _logger?.LogWarning("USB enum registry key not found");
                return results;
            }

            foreach (var vidPidName in usbEnumKey.GetSubKeyNames())
            {
                using var vidPidKey = usbEnumKey.OpenSubKey(vidPidName);
                if (vidPidKey == null) continue;

                foreach (var instanceName in vidPidKey.GetSubKeyNames())
                {
                    using var instanceKey = vidPidKey.OpenSubKey(instanceName);
                    if (instanceKey == null) continue;

                    using var deviceParams = instanceKey.OpenSubKey("Device Parameters");
                    var portName = deviceParams?.GetValue("PortName")?.ToString();

                    if (string.IsNullOrEmpty(portName) ||
                        !portName.StartsWith("COM", StringComparison.OrdinalIgnoreCase))
                        continue;

                    var friendlyName = instanceKey.GetValue("FriendlyName")?.ToString() ?? "";
                    var deviceId = $@"USB\{vidPidName}\{instanceName}";

                    var info = new UsbSerialPortInfo
                    {
                        PortName = portName,
                        Description = friendlyName,
                        DeviceId = deviceId
                    };

                    ParseWindowsVidPidSerial(vidPidName, info);

                    if (!instanceName.Contains('&'))
                        info = info with { SerialNumber = instanceName };

                    results.Add(info);

                    _logger?.LogInformation(
                        "Discovered USB serial port {PortName} VID={VendorId} PID={ProductId} SN={SerialNumber} Desc={Description}",
                        info.PortName,
                        info.VendorId,
                        info.ProductId,
                        info.SerialNumber,
                        info.Description);
                }
            }

            results.AddRange(GetFtdiBusSerialPorts(token));
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Registry enumeration failed");
        }

        return results;
    }

    private static IEnumerable<UsbSerialPortInfo> GetFtdiBusSerialPorts(CancellationToken token)
    {
        var results = new List<UsbSerialPortInfo>();

        try
        {
            var ftdiKey = Microsoft.Win32.Registry.LocalMachine
                .OpenSubKey(@"SYSTEM\CurrentControlSet\Enum\FTDIBUS");

            if (ftdiKey == null)
            {
                System.Diagnostics.Debug.WriteLine(
                    "[UsbSerialPortMapper] FTDIBUS key not found — no FTDI devices");
                return results;
            }

            foreach (var deviceName in ftdiKey.GetSubKeyNames())
            {
                using var deviceKey = ftdiKey.OpenSubKey(deviceName);
                if (deviceKey == null) continue;

                foreach (var instanceName in deviceKey.GetSubKeyNames())
                {
                    using var instanceKey = deviceKey.OpenSubKey(instanceName);
                    if (instanceKey == null) continue;

                    using var deviceParams = instanceKey.OpenSubKey("Device Parameters");
                    var portName = deviceParams?.GetValue("PortName")?.ToString();

                    if (string.IsNullOrEmpty(portName) ||
                        !portName.StartsWith("COM", StringComparison.OrdinalIgnoreCase))
                        continue;

                    var friendlyName = instanceKey.GetValue("FriendlyName")?.ToString() ?? "";

                    var info = new UsbSerialPortInfo
                    {
                        PortName = portName,
                        Description = friendlyName,
                        DeviceId = $@"FTDIBUS\{deviceName}\{instanceName}"
                    };

                    info = ParseFtdiDeviceName(deviceName, info);
                    results.Add(info);

                    System.Diagnostics.Debug.WriteLine(
                        $"[FTDIBUS] Port={info.PortName} VID={info.VendorId} " +
                        $"PID={info.ProductId} SN={info.SerialNumber} Desc={info.Description}");
                }
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine(
                $"[UsbSerialPortMapper] FTDIBUS enumeration failed: {ex.Message}");
        }

        return results;
    }

    private static UsbSerialPortInfo ParseWindowsVidPidSerial(
        string vidPidSegment, UsbSerialPortInfo info)
    {
        foreach (var token in vidPidSegment.Split('&'))
        {
            if (token.StartsWith("VID_", StringComparison.OrdinalIgnoreCase))
                info = info with { VendorId = token[4..] };
            else if (token.StartsWith("PID_", StringComparison.OrdinalIgnoreCase))
                info = info with { ProductId = token[4..] };
        }

        return info;
    }

    private static UsbSerialPortInfo ParseFtdiDeviceName(
        string deviceName, UsbSerialPortInfo info)
    {
        foreach (var token in deviceName.Split('+'))
        {
            if (token.StartsWith("VID_", StringComparison.OrdinalIgnoreCase))
                info = info with { VendorId = token[4..] };
            else if (token.StartsWith("PID_", StringComparison.OrdinalIgnoreCase))
                info = info with { ProductId = token[4..] };
            else if (!token.Contains('_') && !string.IsNullOrWhiteSpace(token))
                info = info with { SerialNumber = token };
        }

        return info;
    }

    // ---------------- LINUX ----------------

    private static IReadOnlyList<UsbSerialPortInfo> GetLinuxUsbSerialPorts(CancellationToken token)
    {
        var results = new List<UsbSerialPortInfo>();

        try
        {
            var ttyRoot = "/sys/class/tty";

            if (!Directory.Exists(ttyRoot))
            {
                _logger?.LogDebug("[UsbSerialPortMapper] /sys/class/tty not found");
                return results;
            }

            // Cache the /dev/serial/by-id directory entries if it exists to easily match persistent paths
            var byIdMapping = new Dictionary<string, string>();
            var byIdRoot = "/dev/serial/by-id";
            if (Directory.Exists(byIdRoot))
            {
                foreach (var file in Directory.GetFiles(byIdRoot))
                {
                    var target = ResolveSysfsPath(file);
                    if (target != null)
                    {
                        var targetName = Path.GetFileName(target);
                        if (!byIdMapping.ContainsKey(targetName))
                        {
                            byIdMapping[targetName] = file;
                        }
                    }
                }
            }

            foreach (var ttyPath in Directory.GetDirectories(ttyRoot))
            {
                token.ThrowIfCancellationRequested();

                var ttyName = Path.GetFileName(ttyPath);

                if (!ttyName.StartsWith("ttyUSB", StringComparison.Ordinal) &&
                    !ttyName.StartsWith("ttyACM", StringComparison.Ordinal) &&
                    !ttyName.StartsWith("ttyAMA", StringComparison.Ordinal))
                    continue;

                var realPath = ResolveSysfsPath(ttyPath);
                if (realPath == null)
                {
                    _logger?.LogDebug($"[UsbSerialPortMapper] Could not resolve sysfs path for {ttyName}");
                    continue;
                }

                var usbDevicePath = FindUsbDeviceNode(realPath);
                if (usbDevicePath == null)
                {
                    _logger?.LogInformation($"[UsbSerialPortMapper] No USB device node found for {ttyName}");
                    continue;
                }

                // FIX: Check realPath first. If it's not there, climb to parent to find 'bInterfaceNumber'
                string? interfaceNum = ReadSysfsFile(realPath, "bInterfaceNumber");
                if (string.IsNullOrEmpty(interfaceNum))
                {
                    var parentDir = Directory.GetParent(realPath);
                    if (parentDir != null)
                    {
                        interfaceNum = ReadSysfsFile(parentDir.FullName, "bInterfaceNumber");
                    }
                }

                // Final fallback if the system doesn't expose it
                interfaceNum ??= "00";

                string rawSerial = ReadSysfsFile(usbDevicePath, "serial") ?? "UnknownSerial";

                // Determine if a persistent /dev/serial/by-id shortcut exists for this port
                string persistentPortName = byIdMapping.TryGetValue(ttyName, out var matchedPath)
                    ? matchedPath
                    : $"/dev/{ttyName}";

                var info = new UsbSerialPortInfo
                {
                    PortName = persistentPortName,
                    VendorId = ReadSysfsFile(usbDevicePath, "idVendor"),
                    ProductId = ReadSysfsFile(usbDevicePath, "idProduct"),

                    // This will now successfully output "BP04126-01-00", "BP04126-01-01", etc.
                    SerialNumber = $"{rawSerial}-{interfaceNum}",

                    Description = ReadSysfsFile(usbDevicePath, "product"),
                    DeviceId = usbDevicePath
                };

                // Recalculate your downstream tracking 'Key' property inside your wrapper if needed
                // e.g., info.Key = $"{info.VendorId}:{info.ProductId}:{info.DeviceId}:{info.SerialNumber}";

                results.Add(info);

                _logger?.LogDebug(
                    $"[sysfs] Port={info.PortName} VID={info.VendorId} " +
                    $"PID={info.ProductId} SN={info.SerialNumber} " +
                    $"Desc={info.Description}");
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger?.LogError($"[UsbSerialPortMapper] Linux enumeration failed: {ex.Message}");
        }

        return results;
    }

    private static string? ResolveSysfsPath(string path)
    {
        try
        {
            var deviceLink = Path.Combine(path, "device");
            string targetLink = Directory.Exists(deviceLink) ? deviceLink : path;

            if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            {
                var startInfo = new ProcessStartInfo
                {
                    FileName = "readlink",
                    Arguments = $"-f \"{targetLink}\"",
                    RedirectStandardOutput = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };

                using var process = Process.Start(startInfo);
                if (process != null)
                {
                    string output = process.StandardOutput.ReadToEnd().Trim();
                    process.WaitForExit();
                    if (Directory.Exists(output) || File.Exists(output)) return output;
                }
            }

            var resolved = new DirectoryInfo(targetLink).ResolveLinkTarget(returnFinalTarget: true);
            return resolved?.FullName;
        }
        catch
        {
            return null;
        }
    }



    private static string? FindUsbDeviceNode(string startPath)
    {
        _logger?.LogDebug(
            string.Join(Environment.NewLine, Directory.GetFileSystemEntries(startPath)));

        var current = new DirectoryInfo(startPath);

        while (current != null && current.Exists)
        {

            if (File.Exists(Path.Combine(current.FullName, "idVendor")))
                return current.FullName;

            current = current.Parent;
        }

        return null;
    }


    private static string ReadSysfsFile(string basePath, string fileName)
    {
        try
        {
            var path = Path.Combine(basePath, fileName);
            return File.Exists(path)
                ? File.ReadAllText(path).Trim()
                : "";
        }
        catch
        {
            return "";
        }
    }
}

// ================================================================
//  UsbPortRegistry — thread-safe, singleton, cross-platform
// ================================================================

/// <summary>
/// Describes the nature of a change raised by <see cref="UsbPortRegistry.PortChanged"/>.
/// </summary>
public enum PortChangeKind { Added, Removed, Updated }

/// <summary>
/// Carries details about a single port change event.
/// </summary>
public sealed class PortChangedEventArgs(
    PortChangeKind kind,
    UsbSerialPortInfo port) : EventArgs
{
    public PortChangeKind Kind { get; } = kind;
    public UsbSerialPortInfo Port { get; } = port;
}

/// <summary>
/// A thread-safe, process-wide registry of discovered USB serial ports.
///
/// Usage:
///   // Populate / refresh (typically on startup or after a hardware change):
///   await UsbPortRegistry.Instance.RefreshAsync();
///
///   // Read from any thread / service:
///   var ports = UsbPortRegistry.Instance.Ports;
///   if (ports.TryGetValue("COM3", out var info)) { ... }
///
///   // Subscribe to live changes:
///   UsbPortRegistry.Instance.PortChanged += (_, e) =>
///       Console.WriteLine($"{e.Kind}: {e.Port.PortName}");
/// </summary>
public sealed class UsbPortRegistry
{
    private ILogger<UsbPortRegistry>? _logger;

    // ---- Singleton ----
    public static UsbPortRegistry Instance { get; } = new();
    private UsbPortRegistry() { }

    public static void ConfigureLogger(ILogger<UsbPortRegistry> logger)
        => Instance._logger = logger;

    // ---- Storage ----
    // Keyed on UsbSerialPortInfo.Key (hardware identity) not PortName,
    // so we correctly detect port reassignment (COM11 → COM12).
    private readonly ConcurrentDictionary<string, UsbSerialPortInfo> _store =
        new(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyDictionary<string, UsbSerialPortInfo> Ports => _store;

    // ---- Change event ----
    public event EventHandler<PortChangedEventArgs>? PortChanged;

    // ---- Refresh guard ----
    private readonly SemaphoreSlim _refreshLock = new(1, 1);

    // ---- Public API ----

    public async Task RefreshAsync(CancellationToken cancellationToken = default)
    {
        await _refreshLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            _logger?.LogInformation("Refreshing USB port registry");

            var discovered = await UsbSerialPortMapper
                .GetUsbSerialPortsAsync(cancellationToken)
                .ConfigureAwait(false);

            ApplySnapshot(discovered);

            _logger?.LogInformation(
                "Discovered {Count} ports from mapper", discovered.Count);
        }
        finally
        {
            _refreshLock.Release();
        }
    }

    public IReadOnlyList<UsbSerialPortInfo> GetSnapshot() =>
        _store.Values.ToArray();

    public bool TryGetPort(string portName, out UsbSerialPortInfo info)
    {
        info = _store.Values.FirstOrDefault(p =>
            string.Equals(p.PortName, portName, StringComparison.OrdinalIgnoreCase))!;
        return info is not null;
    }

    public bool TryGetPortByKey(string key, out UsbSerialPortInfo info) =>
        _store.TryGetValue(key, out info!);

    // ---- Private helpers ----

    private void ApplySnapshot(IReadOnlyList<UsbSerialPortInfo> discovered)
    {
        _logger?.LogDebug(
            "Applying port snapshot. Incoming count: {Count}", discovered.Count);

        foreach (var (k, v) in _store)
            _logger?.LogDebug("Registry store entry: Key={Key} Port={Port}", k, v.PortName);

        foreach (var p in discovered)
            _logger?.LogDebug("Incoming device: Key={Key} Port={Port} VID={VID} PID={PID} SN={SN}",
                p.Key, p.PortName, p.VendorId, p.ProductId, p.SerialNumber);


        // Build incoming map keyed on hardware Key.
        var incoming = discovered
            .Where(p => !string.IsNullOrWhiteSpace(p.Key))
            .ToDictionary(p => p.Key, p => p, StringComparer.OrdinalIgnoreCase);

        // ── Detect removals ───────────────────────────────────────────────────
        foreach (var key in _store.Keys.ToList())
        {
            if (!incoming.ContainsKey(key) && _store.TryRemove(key, out var removed))
            {
                _logger?.LogInformation(
                    "Port removed: {PortName} (Key={Key})",
                    removed.PortName, key);
                RaisePortChanged(PortChangeKind.Removed, removed);
            }
        }

        // ── Detect additions and port-name changes ────────────────────────────
        foreach (var (key, newInfo) in incoming)
        {
            _store.AddOrUpdate(
                key,
                // Add branch — new device
                addKey =>
                {
                    _logger?.LogInformation(
                        "Port added: {PortName} (Key={Key})",
                        newInfo.PortName, key);
                    RaisePortChanged(PortChangeKind.Added, newInfo);
                    return newInfo;
                },
                // Update branch — device already known
                (updateKey, existing) =>
                {
                    // Port name changed (e.g. COM11 → COM12 after replug)
                    if (!string.Equals(
                            existing.PortName,
                            newInfo.PortName,
                            StringComparison.OrdinalIgnoreCase))
                    {
                        _logger?.LogInformation(
                            "Port reassigned: {OldPort} → {NewPort} (Key={Key})",
                            existing.PortName, newInfo.PortName, key);

                        // Raise Removed for the old port name, then Added for
                        // the new one so consumers can close and reopen cleanly.
                        RaisePortChanged(PortChangeKind.Removed, existing);
                        RaisePortChanged(PortChangeKind.Added, newInfo);
                    }
                    else if (!PortInfoEquals(existing, newInfo))
                    {
                        _logger?.LogInformation(
                            "Port updated: {PortName} (Key={Key})",
                            newInfo.PortName, key);
                        RaisePortChanged(PortChangeKind.Updated, newInfo);
                    }

                    return newInfo;
                });
        }
    }

    private void RaisePortChanged(PortChangeKind kind, UsbSerialPortInfo port)
    {
        _logger?.LogDebug(
            "Raising PortChanged event {Kind} for {PortName} (Key={Key})",
            kind, port.PortName, port.Key);
        PortChanged?.Invoke(this, new PortChangedEventArgs(kind, port));
    }

    private static bool PortInfoEquals(UsbSerialPortInfo a, UsbSerialPortInfo b) =>
        string.Equals(a.PortName, b.PortName, StringComparison.OrdinalIgnoreCase) &&
        string.Equals(a.VendorId, b.VendorId, StringComparison.OrdinalIgnoreCase) &&
        string.Equals(a.ProductId, b.ProductId, StringComparison.OrdinalIgnoreCase) &&
        string.Equals(a.SerialNumber, b.SerialNumber, StringComparison.OrdinalIgnoreCase) &&
        string.Equals(a.Description, b.Description, StringComparison.Ordinal) &&
        string.Equals(a.DeviceId, b.DeviceId, StringComparison.Ordinal);
}