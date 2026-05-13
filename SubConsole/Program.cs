using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Serilog;
using Serilog.Core;
using Serilog.Events;
using SubConsole.Models;
using SubConsole.Services;
using SubConsole.Services.Helpers;
using SubConsole.Services.Serial;
using SubConsole.Services.SQL;
using SubConsole.Services.TCP;
using SubConsole.Services.Video;
using System.Runtime.InteropServices;

var levelSwitch = new LoggingLevelSwitch(LogEventLevel.Debug);
try
{
    Directory.CreateDirectory(Path.Combine(AppContext.BaseDirectory, "logs"));

    var host = Host.CreateDefaultBuilder(args)
        .UseSerilog((context, services, configuration) => configuration
            .MinimumLevel.ControlledBy(levelSwitch)
            .ReadFrom.Configuration(context.Configuration)
            .ReadFrom.Services(services)
            .Enrich.FromLogContext())
        .ConfigureServices((context, services) =>
        {
            services.AddSingleton<TcpHostService>();
            services.AddSingleton<SerialPortManagerService>();
            services.AddSingleton<ISerialWorkerFactory, SerialWorkerFactory>();
            services.AddHostedService(provider => provider.GetRequiredService<TcpHostService>());
            services.AddSingleton<SQLiteService>();
            services.AddSingleton<TcpSerialCommandHandler>();
            services.AddSingleton<TcpCameraCommandHandler>();

            if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            {
                services.AddHostedService<UdevMonitorService>();
                if (UdevMonitorService.checkLibudev())
                {
                    throw new DllNotFoundException(
                        "libudev.so.1 could not be found/accessed on this system.");
                }
            }

            services.AddSerialPortManager();
            services.AddCameraManager();                             

            services.AddSingleton(levelSwitch);
            services.Configure<TcpSettings>(
                context.Configuration.GetSection("TcpSettings"));
        })
        .Build();

    var loggerFactory = host.Services.GetRequiredService<ILoggerFactory>();

  
    UsbSerialPortMapper.ConfigureLogger(
        loggerFactory.CreateLogger("UsbSerialPortMapper"));
    UsbPortRegistry.ConfigureLogger(
        loggerFactory.CreateLogger<UsbPortRegistry>());

    
    UsbCameraMapper.ConfigureLogger(
        loggerFactory.CreateLogger("UsbCameraMapper"));
    UsbCameraRegistry.ConfigureLogger(
        loggerFactory.CreateLogger<UsbCameraRegistry>());

    await host.RunAsync();
}
catch (Exception ex)
{
    Log.Fatal(ex, "Application crashed unexpectedly");
}
finally
{
    Log.CloseAndFlush();
}