
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Serilog;
using Serilog.Core;
using Serilog.Events;
using SubConsole.Models;
using SubConsole.Services;
using SubConsole.Services.Serial;
using SubConsole.Services.SQL;
using SubConsole.Services.TCP;
using System.Runtime.InteropServices;

var levelSwitch = new LoggingLevelSwitch(LogEventLevel.Information);

try
{
    Directory.CreateDirectory(Path.Combine(AppContext.BaseDirectory, "logs"));

    await Host.CreateDefaultBuilder(args)
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
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            {
                services.AddHostedService<UdevMonitorService>();
                if(UdevMonitorService.checkLibudev())
                {
                    throw new DllNotFoundException("libudev.so.1 could not be found/accessed on this system.");
                }
            }


            services.AddSerialPortManager();

            services.AddSingleton(levelSwitch);

            services.Configure<TcpSettings>(context.Configuration.GetSection("TcpSettings"));
        })
        .RunConsoleAsync();
}
catch (Exception ex)
{
    Log.Fatal(ex, "Application crashed unexpectedly");
}
finally
{
    Log.CloseAndFlush();
}

