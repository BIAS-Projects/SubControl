using SubConsole.Models;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

#if WINDOWS
using Microsoft.Win32;
#endif

namespace SubConsole.Helpers;

public static class UsbSerialPortMapper
{
    private static readonly SemaphoreSlim _lock = new(1, 1);

    // ---------------- PUBLIC API ----------------

    public static async Task<IReadOnlyList<UsbSerialPortInfo>> GetUsbSerialPortsAsync()
    {
        await _lock.WaitAsync();
        try
        {
            return await Task.Run(() =>
            {
                if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                    return GetWindowsUsbSerialPorts();

                if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
                    return GetLinuxUsbSerialPorts();

                return Array.Empty<UsbSerialPortInfo>();
            });
        }
        finally
        {
            _lock.Release();
        }
    }

    // ---------------- WINDOWS ----------------

    private static IReadOnlyList<UsbSerialPortInfo> GetWindowsUsbSerialPorts()
    {
        var results = new List<UsbSerialPortInfo>();

        try
        {
            // Use reflection to load Microsoft.Win32.Registry so we don't need
            // #if WINDOWS — the RuntimeInformation guard above ensures we only
            // reach this on Windows at runtime
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

            results.AddRange(GetFtdiBusSerialPorts());
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine(
                $"[UsbSerialPortMapper] Registry enumeration failed: {ex.Message}");
        }

        return results;
    }

    private static IEnumerable<UsbSerialPortInfo> GetFtdiBusSerialPorts()
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

    private static IReadOnlyList<UsbSerialPortInfo> GetLinuxUsbSerialPorts()
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

                // Only care about USB serial device types
                if (!ttyName.StartsWith("ttyUSB", StringComparison.Ordinal) &&
                    !ttyName.StartsWith("ttyACM", StringComparison.Ordinal) &&
                    !ttyName.StartsWith("ttyAMA", StringComparison.Ordinal))
                    continue;

                // Resolve the sysfs symlink to get the real device path
                var realPath = ResolveSysfsPath(ttyPath);
                if (realPath == null)
                {
                    System.Diagnostics.Debug.WriteLine(
                        $"[UsbSerialPortMapper] Could not resolve sysfs path for {ttyName}");
                    continue;
                }

                // Walk up the sysfs tree to find the USB device node
                // (the directory containing idVendor / idProduct)
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

    /// <summary>
    /// Resolves the sysfs symlink for a tty entry to its real absolute path.
    /// e.g. /sys/class/tty/ttyUSB0 → /sys/devices/pci.../ttyUSB0
    /// </summary>
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

    /// <summary>
    /// Walks up the sysfs directory tree from a tty device node until it finds
    /// a directory containing "idVendor", which marks the USB device node.
    /// </summary>
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

    /// <summary>
    /// Reads a single-line value from a sysfs attribute file, returning empty
    /// string if the file does not exist or cannot be read.
    /// </summary>
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