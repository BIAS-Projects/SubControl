using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using SubConsole;
using SubConsole.Services;
using System.Runtime.InteropServices;

// Only set GStreamer path on Windows — on Linux it's installed system-wide
if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
{
    Environment.SetEnvironmentVariable("PATH",
        @"C:\gstreamer\1.0\msvc_x86_64\1.0\mingw_x86_64\bin" +
        ";" + Environment.GetEnvironmentVariable("PATH"));
}

Gst.Application.Init();

await Host.CreateDefaultBuilder(args)
    .ConfigureLogging(logging =>
    {
        logging.ClearProviders();
        logging.AddConsole();
    })
    .ConfigureServices(services =>
    {
        services.AddSingleton<TcpHostService>();
        //services.AddSingleton<CommPortService>();
        services.AddSingleton<SerialPortManagerService>();
        services.AddSingleton<WebcamManagerService>();

        // Register as singleton so other services can inject it and subscribe to FrameReceived
        // Port 5001 = first camera (DeviceMonitorService starts at 5000 and increments)
        //services.AddSingleton<WebcamReceiverService>(provider =>
        //{
        //    var logger = provider.GetRequiredService<ILogger<WebcamReceiverService>>();
        //    var service = new WebcamReceiverService(logger, host: "0.0.0.0", port: 5001);

        //    long frameCount = 0;
        //    service.FrameReceived += frame =>
        //    {
        //        var count = Interlocked.Increment(ref frameCount);
        //        if (count % 30 == 0)
        //        {
        //            var (r, g, b) = frame.GetPixel(frame.Width / 2, frame.Height / 2);
        //            Console.WriteLine($"[Frame {count}] {frame.Width}x{frame.Height} | Centre pixel RGB({r},{g},{b})");
        //        }
        //    };

        //    return service;
        //});
        //services.AddHostedService(provider => provider.GetRequiredService<WebcamReceiverService>());

        services.AddHostedService(provider => provider.GetRequiredService<TcpHostService>());
        //services.AddHostedService(provider => provider.GetRequiredService<CommPortService>());
        services.AddHostedService(provider => provider.GetRequiredService<SerialPortManagerService>());
        //services.AddHostedService(provider => provider.GetRequiredService<WebcamManagerService>());

        services.AddHostedService<DeviceMonitorService>();
    })
    .RunConsoleAsync();

public sealed record VideoFrame(int Width, int Height, byte[] Data)
{
    public (byte R, byte G, byte B) GetPixel(int x, int y)
    {
        var offset = (y * Width + x) * 3;
        return (Data[offset], Data[offset + 1], Data[offset + 2]);
    }
}