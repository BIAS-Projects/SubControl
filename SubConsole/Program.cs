using Gst;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using SubConsole;
using SubConsole.Services;
using SubConsole.Services.Serial;

using SubConsole.Services.TCP;
using SubConsole.Services.TCP.Commands;
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
        services.AddSingleton<SQLiteService>();

        //   services.AddSingleton<MyService>();
        services.AddSingleton<TcpCommandInvoker>();

        services.AddSerialPortManager();
        services.AddSingleton<TcpSerialCommandHandler>();

        //Command pattern commands
        services.AddSingleton<ITcpCommand, StartTomAllCommand>();
        services.AddSingleton<ITcpCommand, StopTomAllCommand>();
        services.AddSingleton<ITcpCommand, FlirWhitehotCommand>();
        //services.AddSingleton<ITcpCommand, FlirRainbowCommand>();
        //services.AddSingleton<ITcpCommand, RotorForwardCommand>();
        //services.AddSingleton<ITcpCommand, RotorBackwardCommand>();
        //services.AddSingleton<ITcpCommand, RotorStopCommand>();
        //services.AddSingleton<ITcpCommand, GetUsbPortsCommand>();




    })
    .RunConsoleAsync();

