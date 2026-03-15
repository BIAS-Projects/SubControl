using System.Runtime.InteropServices;

namespace SubConsole.Services;

public static class GStreamerPlatform
{
    public static bool IsWindows => RuntimeInformation.IsOSPlatform(OSPlatform.Windows);
    public static bool IsLinux => RuntimeInformation.IsOSPlatform(OSPlatform.Linux);

    /// <summary>
    /// Returns the best available video source factory name for the current platform.
    /// </summary>
    public static string VideoSourceFactory(string deviceId)
    {
        if (IsWindows)
        {
            bool isMF = deviceId.Contains("mfdevice", StringComparison.OrdinalIgnoreCase);
            return isMF ? "mfvideosrc" : "ksvideosrc";
        }

        // Linux — v4l2src covers virtually all webcams
        return "v4l2src";
    }

    /// <summary>
    /// Returns the best available H264 decoder for the current platform,
    /// trying hardware first then falling back to software.
    /// </summary>
    public static Gst.Element? CreateH264Decoder()
    {
        if (IsWindows)
        {
            return Gst.ElementFactory.Make("d3d11h264dec", "dec")
                ?? Gst.ElementFactory.Make("qsvh264dec", "dec")
                ?? Gst.ElementFactory.Make("openh264dec", "dec");
        }

        // Linux — try VA-API (Intel/AMD), NVDEC (Nvidia), then libav software
        return Gst.ElementFactory.Make("vaapih264dec", "dec")
            ?? Gst.ElementFactory.Make("nvh264dec", "dec")
            ?? Gst.ElementFactory.Make("avdec_h264", "dec");
    }

    /// <summary>
    /// Returns the best available video sink for the current platform.
    /// </summary>
    public static Gst.Element? CreateVideoSink()
    {
        if (IsWindows)
        {
            return Gst.ElementFactory.Make("d3d11videosink", "sink")
                ?? Gst.ElementFactory.Make("autovideosink", "sink");
        }

        // autovideosink works on Linux (picks xvimagesink/glimagesink etc.)
        return Gst.ElementFactory.Make("autovideosink", "sink");
    }

    /// <summary>
    /// Device index property name differs between sources.
    /// </summary>
    public static string DeviceIndexProperty(string sourceFactory) => sourceFactory switch
    {
        "v4l2src" => "device",   // v4l2src uses device path e.g. /dev/video0
        "mfvideosrc" => "device-index",
        "ksvideosrc" => "device-index",
        _ => "device-index"
    };
}