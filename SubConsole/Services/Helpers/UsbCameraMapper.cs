using Microsoft.Extensions.Logging;
using Microsoft.Win32;
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

#if WINDOWS
using Microsoft.Win32;
#endif

namespace SubConsole.Services.Helpers;

public static class UsbCameraMapper
{
    private static readonly SemaphoreSlim _lock = new(1, 1);
    private static ILogger? _logger;

    public static void ConfigureLogger(ILogger logger)
    {
        _logger = logger;
    }

    // ---------------- PUBLIC API ----------------

    public static async Task<IReadOnlyList<UsbCameraInfo>> GetUsbCamerasAsync(CancellationToken token)
    {
        await _lock.WaitAsync(token);
        try
        {
            return await Task.Run(() =>
            {
                if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                {
                    _logger?.LogInformation("Starting Windows USB camera scan");
                    return GetWindowsUsbCameras(token);
                }

                if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
                {
                    _logger?.LogInformation("Starting Linux USB camera scan");
                    return GetLinuxUsbCameras(token);
                }

                _logger?.LogWarning("No cameras found during scan — unsupported platform");
                return Array.Empty<UsbCameraInfo>();
            }, token);
        }
        finally
        {
            _lock.Release();
        }
    }

    public static async Task<string> GetUsbCamerasAsJsonAsync(CancellationToken token)
    {
        var cameras = await GetUsbCamerasAsync(token);
        return JsonSerializer.Serialize(cameras, new JsonSerializerOptions { WriteIndented = true });
    }

    // ---------------- WINDOWS ----------------

    private static IReadOnlyList<UsbCameraInfo> GetWindowsUsbCameras(CancellationToken token)
    {
        var results = new List<UsbCameraInfo>();

        try
        {
            // Cameras appear under the USB enum key just like serial ports,
            // but we also check the dedicated imaging / camera device class keys.
            results.AddRange(ScanWindowsDeviceClass(
                @"SYSTEM\CurrentControlSet\Enum\USB",
                isCamera: IsUsbCameraDevice));

            results.AddRange(ScanWindowsDeviceClass(
                @"SYSTEM\CurrentControlSet\Enum\PCI",
                isCamera: IsUsbCameraDevice));
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Windows USB camera enumeration failed");
        }

        return results;
    }

    private static IEnumerable<UsbCameraInfo> ScanWindowsDeviceClass(
        string registryPath, Func<RegistryKey, bool> isCamera)
    {
        var results = new List<UsbCameraInfo>();

        var hklm = Registry.LocalMachine;
        var rootKey = hklm.OpenSubKey(registryPath);
        if (rootKey == null)
        {
            _logger?.LogWarning("Registry key not found: {Path}", registryPath);
            return results;
        }

        foreach (var vidPidName in rootKey.GetSubKeyNames())
        {
            using var vidPidKey = rootKey.OpenSubKey(vidPidName);
            if (vidPidKey == null) continue;

            foreach (var instanceName in vidPidKey.GetSubKeyNames())
            {
                using var instanceKey = vidPidKey.OpenSubKey(instanceName);
                if (instanceKey == null) continue;

                if (!isCamera(instanceKey)) continue;

                var friendlyName = instanceKey.GetValue("FriendlyName")?.ToString()
                                   ?? instanceKey.GetValue("DeviceDesc")?.ToString()
                                   ?? "";
                var deviceId = $@"USB\{vidPidName}\{instanceName}";

                var info = new UsbCameraInfo
                {
                    FriendlyName = friendlyName,
                    DeviceId = deviceId
                };

                info = ParseWindowsVidPid(vidPidName, info);

                if (!instanceName.Contains('&'))
                    info = info with { SerialNumber = instanceName };

                // Resolve the symbolic link name used by DirectShow / Media Foundation
                using var deviceParams = instanceKey.OpenSubKey("Device Parameters");
                var symbolicLink = deviceParams?.GetValue("SymbolicLink")?.ToString() ?? "";
                info = info with { SymbolicLink = symbolicLink };

                results.Add(info);

                _logger?.LogInformation(
                    "Discovered USB camera VID={VendorId} PID={ProductId} SN={SerialNumber} Desc={FriendlyName}",
                    info.VendorId,
                    info.ProductId,
                    info.SerialNumber,
                    info.FriendlyName);
            }
        }

        return results;
    }

    /// <summary>
    /// Returns true when the registry instance key looks like a video capture device.
    /// Checks the device class GUID and/or friendly name heuristics.
    /// </summary>
    private static bool IsUsbCameraDevice(RegistryKey instanceKey)
    {
        // Device class GUIDs for imaging / cameras / media capture
        // {6bdd1fc6-...} = Image     {ca3e7ab9-...} = Camera (Win10+)
        const string imageClassGuid = "{6bdd1fc6-810f-11d0-bec7-08002be2092f}";
        const string cameraClassGuid = "{ca3e7ab9-b4c3-4ae6-8251-579ef933890f}";

        var classGuid = instanceKey.GetValue("ClassGUID")?.ToString() ?? "";
        if (string.Equals(classGuid, imageClassGuid, StringComparison.OrdinalIgnoreCase)) return true;
        if (string.Equals(classGuid, cameraClassGuid, StringComparison.OrdinalIgnoreCase)) return true;

        // Fallback — friendly name contains "camera" or "webcam"
        var friendly = instanceKey.GetValue("FriendlyName")?.ToString() ?? "";
        return friendly.Contains("camera", StringComparison.OrdinalIgnoreCase)
            || friendly.Contains("webcam", StringComparison.OrdinalIgnoreCase)
            || friendly.Contains("capture", StringComparison.OrdinalIgnoreCase);
    }

    private static UsbCameraInfo ParseWindowsVidPid(string vidPidSegment, UsbCameraInfo info)
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

    // ---------------- LINUX ----------------

    private static IReadOnlyList<UsbCameraInfo> GetLinuxUsbCameras(CancellationToken token)
    {
        var results = new List<UsbCameraInfo>();

        try
        {
            const string videoRoot = "/sys/class/video4linux";

            if (!Directory.Exists(videoRoot))
            {
                _logger?.LogWarning("/sys/class/video4linux not found — no V4L2 devices");
                return results;
            }

            foreach (var videoPath in Directory.GetDirectories(videoRoot))
            {
                token.ThrowIfCancellationRequested();

                var videoName = Path.GetFileName(videoPath); // e.g. video0

                var realPath = ResolveSysfsPath(videoPath);
                if (realPath == null)
                {
                    _logger?.LogDebug("Could not resolve sysfs path for {Device}", videoName);
                    continue;
                }

                // Only keep capture-capable nodes (skip metadata / output nodes)
                if (!IsVideoCaptureNode(realPath))
                    continue;

                var usbDevicePath = FindUsbDeviceNode(realPath);
                if (usbDevicePath == null)
                {
                    _logger?.LogDebug("No USB device node found for {Device}", videoName);
                    continue;
                }

                var info = new UsbCameraInfo
                {
                    DevicePath = $"/dev/{videoName}",
                    VendorId = ReadSysfsFile(usbDevicePath, "idVendor"),
                    ProductId = ReadSysfsFile(usbDevicePath, "idProduct"),
                    SerialNumber = ReadSysfsFile(usbDevicePath, "serial"),
                    FriendlyName = ReadSysfsFile(usbDevicePath, "product"),
                    DeviceId = usbDevicePath
                };

                results.Add(info);

                _logger?.LogInformation(
                    "Discovered USB camera {DevicePath} VID={VendorId} PID={ProductId} SN={SerialNumber} Desc={FriendlyName}",
                    info.DevicePath,
                    info.VendorId,
                    info.ProductId,
                    info.SerialNumber,
                    info.FriendlyName);
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Linux USB camera enumeration failed");
        }

        // Filter out duplicate video nodes mapping to the same physical device ID
        return results.DistinctBy(c => c.DeviceId).ToList();
    }

    /// <summary>
    /// Checks the V4L2 capabilities file to confirm this is a capture node,
    /// not a metadata or output-only node.
    /// </summary>
    //private static bool IsVideoCaptureNode(string sysfsPath)
    //{
    //    // The "name" file is present on real capture nodes; metadata nodes
    //    // often land under a different subdirectory.  A belt-and-braces check
    //    // is to look for the capabilities file exposed by the driver.
    //    var namePath = Path.Combine(sysfsPath, "name");
    //    return File.Exists(namePath); // crude but effective for most drivers
    //}

    private static bool IsVideoCaptureNode(string videoPath)
    {
        try
        {
            string namePath = Path.Combine(videoPath, "name");
            if (File.Exists(namePath))
            {
                string name = File.ReadAllText(namePath);

                // Case-insensitive checks without altering the original string
                if (name.Contains("metadata", StringComparison.OrdinalIgnoreCase) ||
                    name.Contains("telemetry", StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }
            }
            return true;
        }
        catch
        {
            return false;
        }
    }



    private static string? ResolveSysfsPath(string path)
    {
        try
        {
            var deviceLink = Path.Combine(path, "device");

            // Fix: Handles both /sys/class folder structures and direct symlink files cleanly
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
            return File.Exists(path) ? File.ReadAllText(path).Trim() : "";
        }
        catch
        {
            return "";
        }
    }
}

// ================================================================
//  UsbCameraInfo — immutable record
// ================================================================

public sealed record UsbCameraInfo
{
    /// <summary>/dev/video0 (Linux) or empty string (Windows — use SymbolicLink).</summary>
    public string DevicePath { get; init; } = "";

    /// <summary>DirectShow / Media Foundation symbolic link, e.g. \\?\usb#vid_...  (Windows only).</summary>
    public string SymbolicLink { get; init; } = "";

    public string FriendlyName { get; init; } = "";
    public string VendorId { get; init; } = "";
    public string ProductId { get; init; } = "";
    public string SerialNumber { get; init; } = "";
    public string DeviceId { get; init; } = "";
}

// ================================================================
//  UsbCameraRegistry — thread-safe singleton
// ================================================================

public enum CameraChangeKind { Added, Removed, Updated }

public sealed class CameraChangedEventArgs(
    CameraChangeKind kind,
    UsbCameraInfo camera) : EventArgs
{
    public CameraChangeKind Kind { get; } = kind;
    public UsbCameraInfo Camera { get; } = camera;
}

/// <summary>
/// Thread-safe, process-wide registry of discovered USB cameras.
///
/// Usage:
///   await UsbCameraRegistry.Instance.RefreshAsync();
///
///   var cameras = UsbCameraRegistry.Instance.Cameras;
///
///   UsbCameraRegistry.Instance.CameraChanged += (_, e) =>
///       logger.LogInformation("{Kind}: {Name}", e.Kind, e.Camera.FriendlyName);
/// </summary>
public sealed class UsbCameraRegistry
{
    // ---- Singleton ----
    public static UsbCameraRegistry Instance { get; } = new();
    private UsbCameraRegistry() { }

    private ILogger<UsbCameraRegistry>? _logger;

    public static void ConfigureLogger(ILogger<UsbCameraRegistry> logger)
    {
        Instance._logger = logger;
    }

    // ---- Storage ----
    // Keyed by DeviceId (stable across plug/unplug cycles unlike /dev/videoN indices).
    private readonly ConcurrentDictionary<string, UsbCameraInfo> _store = new();

    public IReadOnlyDictionary<string, UsbCameraInfo> Cameras => _store;

    // ---- Change event ----
    public event EventHandler<CameraChangedEventArgs>? CameraChanged;

    // ---- Refresh guard ----
    private readonly SemaphoreSlim _refreshLock = new(1, 1);

    // ---- Public API ----

    public async Task RefreshAsync(CancellationToken cancellationToken = default)
    {
        await _refreshLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            _logger?.LogInformation("Refreshing USB camera registry");

            var discovered = await UsbCameraMapper
                .GetUsbCamerasAsync(cancellationToken)
                .ConfigureAwait(false);

            ApplySnapshot(discovered);

            _logger?.LogInformation("Discovered {Count} cameras from mapper", discovered.Count);
        }
        finally
        {
            _refreshLock.Release();
        }
    }

    public IReadOnlyList<UsbCameraInfo> GetSnapshot() => _store.Values.ToArray();

    public bool TryGetCamera(string deviceId, out UsbCameraInfo info) =>
        _store.TryGetValue(deviceId, out info!);

    // ---- Private helpers ----

    private void ApplySnapshot(IReadOnlyList<UsbCameraInfo> discovered)
    {
        _logger?.LogDebug("Applying camera snapshot. Incoming count: {Count}", discovered.Count);

        var incoming = discovered.ToDictionary(c => c.DeviceId);

        // --- Removals ---
        foreach (var key in _store.Keys)
        {
            if (!incoming.ContainsKey(key) && _store.TryRemove(key, out var removed))
            {
                RaiseCameraChanged(CameraChangeKind.Removed, removed);
                _logger?.LogInformation("Camera removed: {FriendlyName}", removed.FriendlyName);
            }
        }

        // --- Additions and updates ---
        foreach (var (key, newInfo) in incoming)
        {
            _store.AddOrUpdate(
                key,
                addKey =>
                {
                    _logger?.LogInformation("Camera added: {FriendlyName}", newInfo.FriendlyName);
                    RaiseCameraChanged(CameraChangeKind.Added, newInfo);
                    return newInfo;
                },
                (updateKey, existing) =>
                {
                    _logger?.LogInformation("Camera updated: {FriendlyName}", newInfo.FriendlyName);
                    if (!CameraInfoEquals(existing, newInfo))
                        RaiseCameraChanged(CameraChangeKind.Updated, newInfo);
                    return newInfo;
                });
        }
    }

    private void RaiseCameraChanged(CameraChangeKind kind, UsbCameraInfo camera)
    {
        _logger?.LogDebug("Raising CameraChanged event {Kind} for {FriendlyName}", kind, camera.FriendlyName);
        CameraChanged?.Invoke(this, new CameraChangedEventArgs(kind, camera));
    }

    private static bool CameraInfoEquals(UsbCameraInfo a, UsbCameraInfo b) =>
        string.Equals(a.DevicePath, b.DevicePath, StringComparison.Ordinal) &&
        string.Equals(a.SymbolicLink, b.SymbolicLink, StringComparison.Ordinal) &&
        string.Equals(a.FriendlyName, b.FriendlyName, StringComparison.Ordinal) &&
        string.Equals(a.VendorId, b.VendorId, StringComparison.OrdinalIgnoreCase) &&
        string.Equals(a.ProductId, b.ProductId, StringComparison.OrdinalIgnoreCase) &&
        string.Equals(a.SerialNumber, b.SerialNumber, StringComparison.OrdinalIgnoreCase) &&
        string.Equals(a.DeviceId, b.DeviceId, StringComparison.Ordinal);
}