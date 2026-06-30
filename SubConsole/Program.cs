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
        // FIX 1: Hook the systemd lifetime manager into the host builder chain
        .UseSystemd()
        .ConfigureServices((context, services) =>
        {
            services.AddSingleton<TcpHostService>();
            services.AddSingleton<SerialPortManagerService>();
            services.AddSingleton<ISerialWorkerFactory, SerialWorkerFactory>();
            services.AddHostedService(
                provider => provider.GetRequiredService<TcpHostService>());
            services.AddSingleton<SQLiteService>();
            services.AddSingleton<TcpSerialCommandHandler>();
            services.AddSingleton<TcpCameraCommandHandler>();

            // ── USB port monitor — runtime OS check, no compile-time guards ───────
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            {
                // Fail fast if libudev isn't available rather than starting a
                // monitor that will immediately error out in ExecuteAsync.
                if (!UdevMonitorService.CheckLibudev())
                {
                    throw new DllNotFoundException(
                        "libudev.so.1 could not be found/accessed on this system.");
                }

                services.AddHostedService<UdevMonitorService>();
            }
            else if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                services.AddHostedService<WmiMonitorService>();
            }

            // NOTE: REMOVED services.AddSystemd() from here as it belongs on the host builder chain above.

            services.AddSerialPortManager(context.Configuration);
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
    throw;
}
finally
{
    Log.CloseAndFlush();
}