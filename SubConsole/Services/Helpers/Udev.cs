using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;

namespace SubConsole.Services.Helpers
{
    internal static class Udev
    {
        private const string Lib = "libudev.so.1";

        [DllImport(Lib)] public static extern IntPtr udev_new();
        [DllImport(Lib)] public static extern IntPtr udev_monitor_new_from_netlink(IntPtr udev, string name);
        [DllImport(Lib)] public static extern int udev_monitor_filter_add_match_subsystem_devtype(IntPtr monitor, string subsystem, string devtype);
        [DllImport(Lib)] public static extern int udev_monitor_enable_receiving(IntPtr monitor);
        [DllImport(Lib)] public static extern int udev_monitor_get_fd(IntPtr monitor);
        [DllImport(Lib)] public static extern IntPtr udev_monitor_receive_device(IntPtr monitor);

        [DllImport(Lib)] public static extern IntPtr udev_device_get_action(IntPtr device);
        [DllImport(Lib)] public static extern IntPtr udev_device_get_devnode(IntPtr device);

        [DllImport(Lib)] public static extern void udev_device_unref(IntPtr device);
        [DllImport(Lib)] public static extern void udev_unref(IntPtr udev);
    }
}
