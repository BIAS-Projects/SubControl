using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using SubConsole;
using SubConsole.Services;


Environment.SetEnvironmentVariable("PATH",
    @"C:\gstreamer\1.0\msvc_x86_64\1.0\mingw_x86_64\bin" + Environment.GetEnvironmentVariable("PATH"));


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



        services.AddHostedService(provider => provider.GetRequiredService<TcpHostService>());
       // services.AddHostedService(provider => provider.GetRequiredService<CommPortService>());
        services.AddHostedService(provider => provider.GetRequiredService<SerialPortManagerService>());
       // services.AddHostedService(provider => provider.GetRequiredService<WebcamManagerService>());
        services.AddHostedService<DeviceMonitorService>();
    })
    .RunConsoleAsync();