using Gst;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using SubConsole;
using SubConsole.Services;
using SubConsole.Services.Serial;
using SubConsole.Services.Serial.Factories;
using SubControlMAUI.Services;
using System.Runtime.InteropServices;
using System.Timers;



await Host.CreateDefaultBuilder(args)
    .ConfigureLogging(logging =>
    {
        logging.ClearProviders();
        logging.AddConsole();
    })
    .ConfigureServices(services =>
    {
        services.AddSingleton<TcpHostService>();
        services.AddSingleton<SerialPortManagerService>();
        services.AddSingleton<ISerialWorkerFactory, SerialWorkerFactory>();
        services.AddHostedService(provider => provider.GetRequiredService<TcpHostService>());
        services.AddHostedService(provider => provider.GetRequiredService<SerialPortManagerService>());
        services.AddSingleton<SQLiteService>();
    })
    .RunConsoleAsync();

