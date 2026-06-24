using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using SubConsole.Models;
using SubConsole.Services.Helpers;
using System.Runtime.InteropServices;

#if WINDOWS
using System.Management;
using System.Text.RegularExpressions;
#endif

namespace SubConsole.Services;

// ═════════════════════════════════════════════════════════════════════════════
// Platform-agnostic base
// ═════════════════════════════════════════════════════════════════════════════

/// <summary>
/// Monitors the OS for USB serial device arrivals and removals and forwards
/// the events to <see cref="UsbPortRegistry"/> (which in turn raises
/// <see cref="UsbPortRegistry.PortChanged"/> for <c>TcpHostService</c> and
/// <c>SerialPortManagerService</c> to handle).
///
/// Platform selection is done at DI registration time in <c>Program.cs</c>:
///   Linux   → <see cref="UdevMonitorService"/>
///   Windows → <see cref="WmiMonitorService"/>
/// </summary>
public abstract class UsbMonitorServiceBase : BackgroundService
{
    protected readonly ILogger Logger;

    protected UsbMonitorServiceBase(ILogger logger)
    {
        Logger = logger;
    }

    /// <summary>
    /// Triggers a full registry refresh and lets <see cref="UsbPortRegistry"/>
    /// diff the result against its current snapshot, raising
    /// <see cref="PortChangedEventArgs"/> for every add / remove it detects.
    /// </summary>
    protected async Task RefreshRegistryAsync(
        string triggerReason, CancellationToken token)
    {
        Logger.LogDebug(
            "Triggering USB port registry refresh. Reason: {Reason}",
            triggerReason);

        try
        {
            await UsbPortRegistry.Instance.RefreshAsync(token);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            Logger.LogError(
                ex,
                "USB port registry refresh failed. Reason: {Reason}",
                triggerReason);
        }
    }
}

// ═════════════════════════════════════════════════════════════════════════════
// Linux — udev via native libudev
// ═════════════════════════════════════════════════════════════════════════════

public sealed class UdevMonitorService : UsbMonitorServiceBase
{
    public UdevMonitorService(ILogger<UdevMonitorService> logger)
        : base(logger) { }

    // ── libudev availability check (called from Program.cs) ───────────────────

    public static bool CheckLibudev()
    {
        try
        {
            IntPtr handle = NativeLibrary.Load("libudev.so.1");
            NativeLibrary.Free(handle);
            return true;
        }
        catch
        {
            return false;
        }
    }

    // ── BackgroundService ─────────────────────────────────────────────────────

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            Logger.LogInformation(
                "Udev monitor not started: non-Linux platform");
            return;
        }

        if (!CheckLibudev())
        {
            Logger.LogError(
                "Udev monitor cannot start: libudev.so.1 not available");
            return;
        }

        IntPtr udev = IntPtr.Zero;
        IntPtr monitor = IntPtr.Zero;

        try
        {
            Logger.LogDebug("Initialising udev monitor");

            udev = Udev.udev_new();
            monitor = Udev.udev_monitor_new_from_netlink(udev, "udev");

            // Monitor USB serial devices
            Udev.udev_monitor_filter_add_match_subsystem_devtype(monitor, "tty", null);
            // Monitor video (camera) devices
            Udev.udev_monitor_filter_add_match_subsystem_devtype(monitor, "video4linux", null);

            Udev.udev_monitor_enable_receiving(monitor);

            var fd = Udev.udev_monitor_get_fd(monitor);
            var buffer = new byte[1];

            Logger.LogInformation("Udev monitor started (fd={Fd})", fd);

            while (!stoppingToken.IsCancellationRequested)
            {
                int read = 0;

                try
                {
                    // read() blocks until the kernel signals activity on the
                    // netlink socket.  Offload to the thread pool so we do not
                    // tie up an async scheduler thread indefinitely.
                    read = await Task.Run(
                        () => ReadFd(fd, buffer, 1),
                        stoppingToken);
                }
                catch (OperationCanceledException) { break; }
                catch (Exception ex)
                {
                    Logger.LogError(
                        ex,
                        "Error reading from udev monitor (fd={Fd})",
                        fd);
                    continue;
                }

                if (read <= 0)
                {
                    Logger.LogDebug(
                        "Udev monitor read returned {ReadBytes}",
                        read);
                    continue;
                }

                var device = Udev.udev_monitor_receive_device(monitor);
                if (device == IntPtr.Zero)
                {
                    Logger.LogWarning(
                        "Received udev event with null device");
                    continue;
                }

                try
                {
                    var action = PtrToString(Udev.udev_device_get_action(device));
                    var devNode = PtrToString(Udev.udev_device_get_devnode(device));

                    Logger.LogInformation(
                        "Udev event: {Action} {Device}",
                        action, devNode);

                    await RefreshRegistryAsync(
                        $"udev {action} {devNode}",
                        stoppingToken);
                }
                catch (OperationCanceledException) { break; }
                catch (Exception ex)
                {
                    Logger.LogError(ex, "Error processing udev device event");
                }
                finally
                {
                    Udev.udev_device_unref(device);
                }
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Logger.LogError(ex, "Udev monitor crashed");
        }
        finally
        {
            Logger.LogInformation("Udev monitor stopping");

            // udev_monitor_unref for the monitor handle,
            // udev_unref for the context — these are distinct calls.
            if (monitor != IntPtr.Zero)
            {
                try { Udev.udev_monitor_unref(monitor); } catch { }
            }

            if (udev != IntPtr.Zero)
            {
                try { Udev.udev_unref(udev); } catch { }
            }

            Logger.LogInformation("Udev monitor stopped");
        }
    }

    // ── Native helpers ────────────────────────────────────────────────────────

    private static string PtrToString(System.IntPtr ptr) =>
        ptr == System.IntPtr.Zero
            ? ""
            : System.Runtime.InteropServices.Marshal.PtrToStringAnsi(ptr) ?? "";

    [System.Runtime.InteropServices.DllImport("libc", SetLastError = true)]
    private static extern int read(int fd, byte[] buffer, int count);

    private static int ReadFd(int fd, byte[] buffer, int count) =>
        read(fd, buffer, count);
}

// ── libudev P/Invoke declarations ─────────────────────────────────────────────

internal static class Udev
{
    private const string Lib = "libudev.so.1";

    [System.Runtime.InteropServices.DllImport(Lib)]
    internal static extern System.IntPtr udev_new();

    [System.Runtime.InteropServices.DllImport(Lib)]
    internal static extern void udev_unref(System.IntPtr udev);

    [System.Runtime.InteropServices.DllImport(Lib)]
    internal static extern System.IntPtr udev_monitor_new_from_netlink(
        System.IntPtr udev,
        [System.Runtime.InteropServices.MarshalAs(
            System.Runtime.InteropServices.UnmanagedType.LPStr)] string name);

    [System.Runtime.InteropServices.DllImport(Lib)]
    internal static extern void udev_monitor_unref(System.IntPtr monitor);

    [System.Runtime.InteropServices.DllImport(Lib)]
    internal static extern int udev_monitor_filter_add_match_subsystem_devtype(
        System.IntPtr monitor,
        [System.Runtime.InteropServices.MarshalAs(
            System.Runtime.InteropServices.UnmanagedType.LPStr)] string subsystem,
        [System.Runtime.InteropServices.MarshalAs(
            System.Runtime.InteropServices.UnmanagedType.LPStr)] string? devtype);

    [System.Runtime.InteropServices.DllImport(Lib)]
    internal static extern int udev_monitor_enable_receiving(System.IntPtr monitor);

    [System.Runtime.InteropServices.DllImport(Lib)]
    internal static extern int udev_monitor_get_fd(System.IntPtr monitor);

    [System.Runtime.InteropServices.DllImport(Lib)]
    internal static extern System.IntPtr udev_monitor_receive_device(System.IntPtr monitor);

    [System.Runtime.InteropServices.DllImport(Lib)]
    internal static extern void udev_device_unref(System.IntPtr device);

    [System.Runtime.InteropServices.DllImport(Lib)]
    internal static extern System.IntPtr udev_device_get_action(System.IntPtr device);

    [System.Runtime.InteropServices.DllImport(Lib)]
    internal static extern System.IntPtr udev_device_get_devnode(System.IntPtr device);

    [System.Runtime.InteropServices.DllImport(Lib)]
    internal static extern System.IntPtr udev_device_get_property_value(
        System.IntPtr device,
        [System.Runtime.InteropServices.MarshalAs(
            System.Runtime.InteropServices.UnmanagedType.LPStr)] string key);
}

// ═════════════════════════════════════════════════════════════════════════════
// Windows — WMI PnP event watcher
// ═════════════════════════════════════════════════════════════════════════════

public sealed class WmiMonitorService : UsbMonitorServiceBase
{
    private const double WmiPollIntervalSeconds = 1.0;
    private IDisposable? _watcher;

    public WmiMonitorService(ILogger<WmiMonitorService> logger)
        : base(logger) { }

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            Logger.LogInformation(
                "WMI monitor not started: non-Windows platform");
            return Task.CompletedTask;
        }

        try
        {
            // Use fully qualified names so the compiler resolves them at
            // runtime via the System.Management NuGet package on any TFM.
            var query = new System.Management.WqlEventQuery(
                "__InstanceOperationEvent",
                TimeSpan.FromSeconds(WmiPollIntervalSeconds),
                "TargetInstance ISA 'Win32_PnPEntity' AND " +
                "TargetInstance.Name LIKE '%(COM%'");

            var watcher = new System.Management.ManagementEventWatcher(query);
            watcher.EventArrived += OnEventArrived;
            watcher.Start();
            _watcher = watcher;

            Logger.LogInformation(
                "WMI port monitor started (poll interval {Interval}s)",
                WmiPollIntervalSeconds);

            stoppingToken.Register(() =>
            {
                Logger.LogInformation("WMI port monitor stopping");
                try { watcher.Stop(); } catch { }
                Logger.LogInformation("WMI port monitor stopped");
            });
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "WMI port monitor failed to start");
        }

        return Task.CompletedTask;
    }

    private void OnEventArrived(object sender, System.Management.EventArrivedEventArgs e)
    {
        string deviceName = "(unknown)";
        try
        {
            var eventClass = e.NewEvent.ClassPath.ClassName;
            string action = eventClass switch
            {
                "__InstanceCreationEvent" => "add",
                "__InstanceDeletionEvent" => "remove",
                _ => "unknown"
            };

            if (action == "unknown")
            {
                Logger.LogDebug(
                    "WMI monitor ignoring event class {EventClass}", eventClass);
                return;
            }

            var target = (System.Management.ManagementBaseObject)
                            e.NewEvent["TargetInstance"];
            deviceName = target["Name"]?.ToString() ?? "(unknown)";
            var deviceId = target["DeviceID"]?.ToString() ?? "";

            Logger.LogInformation(
                "WMI port event: {Action} — {DeviceName} ({DeviceId})",
                action, deviceName, deviceId);

            _ = Task.Run(async () =>
            {
                try
                {
                    await RefreshRegistryAsync(
                        $"WMI {action} {deviceName}",
                        CancellationToken.None);
                }
                catch (Exception ex)
                {
                    Logger.LogError(
                        ex,
                        "Registry refresh failed after WMI {Action} event for {DeviceName}",
                        action, deviceName);
                }
            });
        }
        catch (Exception ex)
        {
            Logger.LogError(
                ex,
                "Error processing WMI port event for {DeviceName}",
                deviceName);
        }
    }

    public override void Dispose()
    {
        try { _watcher?.Dispose(); } catch { }
        base.Dispose();
    }
}