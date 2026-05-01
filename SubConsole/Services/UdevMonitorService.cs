using System;
using System.Collections.Generic;
using System.Text;

namespace SubConsole.Services
{
    using Microsoft.Extensions.Hosting;
    using Microsoft.Extensions.Logging;
    using SubConsole.Services.Helpers;
    using System.Runtime.InteropServices;

    public class UdevMonitorService : BackgroundService
    {
        private readonly ILogger<UdevMonitorService> _logger;

        public UdevMonitorService(ILogger<UdevMonitorService> logger)
        {
            _logger = logger;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            if (!RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
                return;

            var udev = Udev.udev_new();
            var monitor = Udev.udev_monitor_new_from_netlink(udev, "udev");

            // Listen only for tty devices (serial ports)
            Udev.udev_monitor_filter_add_match_subsystem_devtype(monitor, "tty", null);
            Udev.udev_monitor_enable_receiving(monitor);

            var fd = Udev.udev_monitor_get_fd(monitor);

            _logger.LogInformation("udev monitor started (fd={Fd})", fd);

            var buffer = new byte[1];

            while (!stoppingToken.IsCancellationRequested)
            {
                // Wait for kernel event (blocking, no polling)
                var read = await Task.Run(() =>
                    read_fd(fd, buffer, 1), stoppingToken);

                if (read > 0)
                {
                    var device = Udev.udev_monitor_receive_device(monitor);
                    if (device == IntPtr.Zero) continue;

                    var actionPtr = Udev.udev_device_get_action(device);
                    var nodePtr = Udev.udev_device_get_devnode(device);

                    var action = PtrToString(actionPtr);
                    var devnode = PtrToString(nodePtr);

                    _logger.LogInformation("udev: {Action} {Device}", action, devnode);

                    // 🔥 Trigger your registry refresh
                    await UsbPortRegistry.Instance.RefreshAsync(stoppingToken);

                    Udev.udev_device_unref(device);
                }
            }

            Udev.udev_unref(udev);
        }

     public static bool checkLibudev()
     {
        try
        {
            IntPtr handle = NativeLibrary.Load("libudev.so.1");
            NativeLibrary.Free(handle);
            return true;
        }
        catch (Exception ex)
        {
            return false;
        }
     }




private static string PtrToString(IntPtr ptr) =>
            ptr == IntPtr.Zero ? "" : Marshal.PtrToStringAnsi(ptr) ?? "";

        [DllImport("libc", SetLastError = true)]
        private static extern int read(int fd, byte[] buffer, int count);

        private static int read_fd(int fd, byte[] buffer, int count) =>
            read(fd, buffer, count);
    }
}
