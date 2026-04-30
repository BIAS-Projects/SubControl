
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Serilog;
using SubConsole.Services.Serial;
using SubConsole.Services.SQL;
using SubConsole.Services.TCP;

try
{
    Directory.CreateDirectory(Path.Combine(AppContext.BaseDirectory, "logs"));

    await Host.CreateDefaultBuilder(args)
        .UseSerilog((context, services, configuration) => configuration
            .ReadFrom.Configuration(context.Configuration)
            .ReadFrom.Services(services)
            .Enrich.FromLogContext())
        .ConfigureServices(services =>
        {
            services.AddSingleton<TcpHostService>();
            services.AddSingleton<SerialPortManagerService>();
            services.AddSingleton<ISerialWorkerFactory, SerialWorkerFactory>();
            services.AddHostedService(provider => provider.GetRequiredService<TcpHostService>());
            services.AddSingleton<SQLiteService>();
            services.AddSingleton<TcpSerialCommandHandler>();

            services.AddSerialPortManager();
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

