using Gst;
using SubConsole.Models;
using System.Collections.Generic;
using System.Diagnostics;
using System.Management;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace SubConsole.Helpers
{

    public static class UsbDeviceEnumerator
    {
        private static readonly SemaphoreSlim _lock = new(1, 1);

        public static async Task<List<UsbDeviceInfo>> GetUsbDevicesAsync()
        {
            await _lock.WaitAsync();

            try
            {
                if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                    return GetWindowsUsbDevices();

                if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
                    return await GetLinuxUsbDevicesAsync();

                return new List<UsbDeviceInfo>();
            }
            finally
            {
                _lock.Release();
            }
        }

        // ---------------- WINDOWS ----------------

        private static List<UsbDeviceInfo> GetWindowsUsbDevices()
        {
            var devices = new List<UsbDeviceInfo>();

            using var searcher = new ManagementObjectSearcher(
                "SELECT * FROM Win32_PnPEntity WHERE DeviceID LIKE 'USB%'");

            foreach (ManagementObject device in searcher.Get())
            {
                string deviceId = device["DeviceID"]?.ToString() ?? "";
                string name = device["Name"]?.ToString() ?? "";

                var info = new UsbDeviceInfo
                {
                    DeviceId = deviceId,
                    Description = name
                };

                ParseVidPid(deviceId, info);

                devices.Add(info);
            }

            return devices;
        }

        // ---------------- LINUX ----------------

        private static async Task<List<UsbDeviceInfo>> GetLinuxUsbDevicesAsync()
        {
            var devices = new List<UsbDeviceInfo>();

            var psi = new ProcessStartInfo
            {
                FileName = "lsusb",
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var process = Process.Start(psi);

            if (process == null)
                return devices;

            string output = await process.StandardOutput.ReadToEndAsync();
            await process.WaitForExitAsync();

            var lines = output.Split('\n');

            foreach (var line in lines)
            {
                if (string.IsNullOrWhiteSpace(line))
                    continue;

                // Example:
                // Bus 002 Device 003: ID 046d:c534 Logitech USB Receiver

                var parts = line.Split(' ');

                int idIndex = System.Array.IndexOf(parts, "ID");

                if (idIndex >= 0 && parts.Length > idIndex + 1)
                {
                    var vidpid = parts[idIndex + 1];
                    var desc = string.Join(" ", parts[(idIndex + 2)..]);

                    var ids = vidpid.Split(':');

                    devices.Add(new UsbDeviceInfo
                    {
                        VendorId = ids.Length > 0 ? ids[0] : "",
                        ProductId = ids.Length > 1 ? ids[1] : "",
                        Description = desc,
                        DeviceId = vidpid
                    });
                }
            }

            return devices;
        }

        // ---------------- HELPERS ----------------

        private static void ParseVidPid(string deviceId, UsbDeviceInfo device)
        {
            if (string.IsNullOrEmpty(deviceId))
                return;

            var parts = deviceId.Split('\\');

            foreach (var part in parts)
            {
                if (part.StartsWith("VID_", StringComparison.OrdinalIgnoreCase))
                    device.VendorId = part.Substring(4);

                if (part.StartsWith("PID_", StringComparison.OrdinalIgnoreCase))
                    device.ProductId = part.Substring(4);
            }
        }



public static class GStreamerDeviceScanner
    {
        private static readonly SemaphoreSlim _lock = new(1, 1);

        public static async Task<List<string>> GetVideoDevicesAsync()
        {
            await _lock.WaitAsync();

            try
            {
                return await System.Threading.Tasks.Task.Run(() =>
                {
                    var devices = new List<string>();

                    var monitor = new DeviceMonitor();
                    monitor.AddFilter("Video/Source", null);
                    monitor.Start();

                    foreach (var device in monitor.Devices)
                    {
                        var props = device.Properties;

                        if (props != null && props.HasField("device.path"))
                        {
                            devices.Add(props.GetString("device.path"));
                        }
                    }

                    monitor.Stop();

                    return devices;
                });
            }
            finally
            {
                _lock.Release();
            }
        }
    }
}
}