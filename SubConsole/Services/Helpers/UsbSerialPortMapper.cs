using Microsoft.Extensions.Logging;
using SubConsole.Models;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

#if WINDOWS
using Microsoft.Win32;
#endif

namespace SubConsole.Services.Helpers;

public static class UsbSerialPortMapper
{
    private static readonly SemaphoreSlim _lock = new(1, 1);

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
                    return GetWindowsUsbSerialPorts(token);

                if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
                    return GetLinuxUsbSerialPorts(token);

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
                System.Diagnostics.Debug.WriteLine(
                    "[UsbSerialPortMapper] USB enum key not found");
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

                    System.Diagnostics.Debug.WriteLine(
                        $"[Registry] Port={info.PortName} VID={info.VendorId} " +
                        $"PID={info.ProductId} SN={info.SerialNumber} Desc={info.Description}");
                }
            }

            results.AddRange(GetFtdiBusSerialPorts(token));
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine(
                $"[UsbSerialPortMapper] Registry enumeration failed: {ex.Message}");
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
                System.Diagnostics.Debug.WriteLine(
                    "[UsbSerialPortMapper] /sys/class/tty not found");
                return results;
            }

            foreach (var ttyPath in Directory.GetDirectories(ttyRoot))
            {
                var ttyName = Path.GetFileName(ttyPath);

                if (!ttyName.StartsWith("ttyUSB", StringComparison.Ordinal) &&
                    !ttyName.StartsWith("ttyACM", StringComparison.Ordinal) &&
                    !ttyName.StartsWith("ttyAMA", StringComparison.Ordinal))
                    continue;

                var realPath = ResolveSysfsPath(ttyPath);
                if (realPath == null)
                {
                    System.Diagnostics.Debug.WriteLine(
                        $"[UsbSerialPortMapper] Could not resolve sysfs path for {ttyName}");
                    continue;
                }

                var usbDevicePath = FindUsbDeviceNode(realPath);
                if (usbDevicePath == null)
                {
                    System.Diagnostics.Debug.WriteLine(
                        $"[UsbSerialPortMapper] No USB device node found for {ttyName}");
                    continue;
                }

                var info = new UsbSerialPortInfo
                {
                    PortName = $"/dev/{ttyName}",
                    VendorId = ReadSysfsFile(usbDevicePath, "idVendor"),
                    ProductId = ReadSysfsFile(usbDevicePath, "idProduct"),
                    SerialNumber = ReadSysfsFile(usbDevicePath, "serial"),
                    Description = ReadSysfsFile(usbDevicePath, "product"),
                    DeviceId = usbDevicePath
                };

                results.Add(info);

                System.Diagnostics.Debug.WriteLine(
                    $"[sysfs] Port={info.PortName} VID={info.VendorId} " +
                    $"PID={info.ProductId} SN={info.SerialNumber} " +
                    $"Desc={info.Description}");
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine(
                $"[UsbSerialPortMapper] Linux enumeration failed: {ex.Message}");
        }

        return results;
    }

    private static string? ResolveSysfsPath(string ttyPath)
    {
        try
        {
            var deviceLink = Path.Combine(ttyPath, "device");
            if (!Directory.Exists(deviceLink)) return null;

            var resolved = new DirectoryInfo(deviceLink)
                .ResolveLinkTarget(returnFinalTarget: true);

            return resolved?.FullName;
        }
        catch
        {
            return null;
        }
    }

    private static string? FindUsbDeviceNode(string startPath)
    {
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
    // ---- Singleton ----

    public static UsbPortRegistry Instance { get; } = new();

    private UsbPortRegistry() { }

    // ---- Storage ----

    // Internal mutable store — all mutations happen under _refreshLock.
    // ConcurrentDictionary gives lock-free reads on the hot path.
    private readonly ConcurrentDictionary<string, UsbSerialPortInfo> _store =
        new(StringComparer.OrdinalIgnoreCase);

    // Public read-only view — zero allocation, safe from any thread.
    public IReadOnlyDictionary<string, UsbSerialPortInfo> Ports => _store;

    // ---- Change event ----

    /// <summary>
    /// Raised on the thread that calls <see cref="RefreshAsync"/> whenever a port
    /// is added, removed, or its metadata changes between two scans.
    /// Subscribers must be thread-safe or marshal to their own context.
    /// </summary>
    public event EventHandler<PortChangedEventArgs>? PortChanged;

    // ---- Refresh guard (prevents concurrent scans) ----

    private readonly SemaphoreSlim _refreshLock = new(1, 1);

    // ---- Public API ----

    /// <summary>
    /// Rescans the host OS for USB serial ports and atomically updates the
    /// registry.  Safe to call from multiple threads — concurrent callers
    /// will queue behind a single semaphore so only one scan runs at a time.
    /// </summary>
    public async Task RefreshAsync(CancellationToken cancellationToken = default)
    {
        await _refreshLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var discovered = await UsbSerialPortMapper
                .GetUsbSerialPortsAsync(cancellationToken)
                .ConfigureAwait(false);

            ApplySnapshot(discovered);
        }
        finally
        {
            _refreshLock.Release();
        }
    }

    /// <summary>
    /// Convenience wrapper: returns a snapshot of <see cref="Ports"/> as an
    /// <see cref="IReadOnlyList{T}"/> without triggering a rescan.
    /// </summary>
    public IReadOnlyList<UsbSerialPortInfo> GetSnapshot() =>
        _store.Values.ToArray();

    /// <summary>
    /// Returns <c>true</c> if a port with the given name is currently known.
    /// Comparison is case-insensitive (COM3 == com3, /dev/ttyUSB0 matches exactly).
    /// </summary>
    public bool TryGetPort(string portName, out UsbSerialPortInfo info) =>
        _store.TryGetValue(portName, out info!);

    // ---- Private helpers ----

    /// <summary>
    /// Merges a freshly scanned list into the registry, firing events for
    /// every add / remove / update.  Called under <see cref="_refreshLock"/>.
    /// </summary>
    private void ApplySnapshot(IReadOnlyList<UsbSerialPortInfo> discovered)
    {
        var incoming = discovered.ToDictionary(
            p => p.PortName,
            p => p,
            StringComparer.OrdinalIgnoreCase);

        // --- Detect removals ---
        foreach (var key in _store.Keys)
        {
            if (!incoming.ContainsKey(key) && _store.TryRemove(key, out var removed))
                RaisePortChanged(PortChangeKind.Removed, removed);
        }

        // --- Detect additions and updates ---
        foreach (var (key, newInfo) in incoming)
        {
            _store.AddOrUpdate(
                key,
                // add branch
                addKey =>
                {
                    RaisePortChanged(PortChangeKind.Added, newInfo);
                    return newInfo;
                },
                // update branch — only raise event if something actually changed
                (updateKey, existing) =>
                {
                    if (!PortInfoEquals(existing, newInfo))
                        RaisePortChanged(PortChangeKind.Updated, newInfo);
                    return newInfo;
                });
        }
    }

    private void RaisePortChanged(PortChangeKind kind, UsbSerialPortInfo port) =>
        PortChanged?.Invoke(this, new PortChangedEventArgs(kind, port));

    /// <summary>
    /// Value-equality check so we avoid spurious <see cref="PortChangeKind.Updated"/>
    /// events when nothing actually changed between two scans.
    /// </summary>
    private static bool PortInfoEquals(UsbSerialPortInfo a, UsbSerialPortInfo b) =>
        string.Equals(a.PortName, b.PortName, StringComparison.OrdinalIgnoreCase) &&
        string.Equals(a.VendorId, b.VendorId, StringComparison.OrdinalIgnoreCase) &&
        string.Equals(a.ProductId, b.ProductId, StringComparison.OrdinalIgnoreCase) &&
        string.Equals(a.SerialNumber, b.SerialNumber, StringComparison.OrdinalIgnoreCase) &&
        string.Equals(a.Description, b.Description, StringComparison.Ordinal) &&
        string.Equals(a.DeviceId, b.DeviceId, StringComparison.Ordinal);
}