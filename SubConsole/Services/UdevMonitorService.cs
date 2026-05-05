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

        //protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        //{
        //    if (!RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        //    {
        //        _logger.LogInformation("Udev monitor not started: non-Linux platform");
        //        return;
        //    }

        //    var udev = Udev.udev_new();
        //    var monitor = Udev.udev_monitor_new_from_netlink(udev, "udev");

        //    // Listen only for tty devices (serial ports)
        //    Udev.udev_monitor_filter_add_match_subsystem_devtype(monitor, "tty", null);
        //    Udev.udev_monitor_enable_receiving(monitor);

        //    var fd = Udev.udev_monitor_get_fd(monitor);

        //    _logger.LogInformation("udev monitor started (fd={Fd})", fd);

        //    var buffer = new byte[1];

        //    while (!stoppingToken.IsCancellationRequested)
        //    {
        //        // Wait for kernel event (blocking, no polling)
        //        var read = await Task.Run(() =>
        //            read_fd(fd, buffer, 1), stoppingToken);

        //        if (read > 0)
        //        {
        //            var device = Udev.udev_monitor_receive_device(monitor);
        //            if (device == IntPtr.Zero) continue;

        //            var actionPtr = Udev.udev_device_get_action(device);
        //            var nodePtr = Udev.udev_device_get_devnode(device);

        //            var action = PtrToString(actionPtr);
        //            var devnode = PtrToString(nodePtr);

        //            _logger.LogInformation("udev: {Action} {Device}", action, devnode);

        //            // 🔥 Trigger your registry refresh
        //            await UsbPortRegistry.Instance.RefreshAsync(stoppingToken);

        //            Udev.udev_device_unref(device);
        //        }
        //    }

        //    Udev.udev_unref(udev);
        //}

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            if (!RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            {
                _logger.LogInformation("Udev monitor not started: non-Linux platform");
                return;
            }

            if (!checkLibudev())
            {
                _logger.LogError("Udev monitor cannot start: libudev not available");
                return;
            }

            IntPtr udev = IntPtr.Zero;
            IntPtr monitor = IntPtr.Zero;

            try
            {
                _logger.LogDebug("Initialising udev monitor");

                udev = Udev.udev_new();
                monitor = Udev.udev_monitor_new_from_netlink(udev, "udev");

                //Monitor serial devices
                Udev.udev_monitor_filter_add_match_subsystem_devtype(monitor, "tty", null);
                //Monitor camera devcies
                Udev.udev_monitor_filter_add_match_subsystem_devtype(monitor, "video4linux", null);

                Udev.udev_monitor_enable_receiving(monitor);

                var fd = Udev.udev_monitor_get_fd(monitor);

                _logger.LogInformation("Udev monitor started (fd={Fd})", fd);

                var buffer = new byte[1];

                while (!stoppingToken.IsCancellationRequested)
                {
                    int read = 0;

                    try
                    {
                        read = await Task.Run(() => read_fd(fd, buffer, 1), stoppingToken);
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Error reading from udev monitor (fd={Fd})", fd);
                        continue;
                    }

                    if (read <= 0)
                    {
                        _logger.LogDebug("Udev monitor read returned {ReadBytes}", read);
                        continue;
                    }

                    var device = Udev.udev_monitor_receive_device(monitor);
                    if (device == IntPtr.Zero)
                    {
                        _logger.LogWarning("Received udev event with null device");
                        continue;
                    }

                    try
                    {
                        var actionPtr = Udev.udev_device_get_action(device);
                        var nodePtr = Udev.udev_device_get_devnode(device);

                        var action = PtrToString(actionPtr);
                        var devnode = PtrToString(nodePtr);

                        _logger.LogInformation(
                            "udev event: {Action} {Device}",
                            action,
                            devnode);

                        _logger.LogDebug(
                            "Triggering USB port registry refresh due to udev event ({Action} {Device})",
                            action,
                            devnode);

                        await UsbPortRegistry.Instance.RefreshAsync(stoppingToken);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Error processing udev device event");
                    }
                    finally
                    {
                        Udev.udev_device_unref(device);
                    }
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError(ex, "Udev monitor crashed");
            }
            finally
            {
                _logger.LogInformation("Udev monitor stopping");

                if (monitor != IntPtr.Zero)
                {
                    try { Udev.udev_unref(monitor); } catch { }
                }

                if (udev != IntPtr.Zero)
                {
                    try { Udev.udev_unref(udev); } catch { }
                }

                _logger.LogInformation("Udev monitor stopped");
            }
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
