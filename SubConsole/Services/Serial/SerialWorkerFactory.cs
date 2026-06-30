using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SubConsole.Models;
using SubConsole.Services.Serial;
using SubConsole.Services.Serial.Workers;
using SubConsole.Services.TCP;
using static SubConsole.Models.UsbDeviceInfo;

namespace SubConsole.Services.Serial;

// ═════════════════════════════════════════════════════════════════════════════
// Worker factory
// ═════════════════════════════════════════════════════════════════════════════

public interface ISerialWorkerFactory
{
    ISerialWorker Create(
        string portPath,
        int baudRate,
        SerialWorkerType type,
        IDeviceRegistry registry);
}

public sealed class SerialWorkerFactory : ISerialWorkerFactory
{
    private readonly ILogger<SerialWorkerFactory> _logger;
    private readonly ILoggerFactory _loggerFactory;

    public SerialWorkerFactory(ILogger<SerialWorkerFactory> logger, ILoggerFactory loggerFactory)
    {
        _logger        = logger;
        _loggerFactory = loggerFactory;
    }

    public ISerialWorker Create(
        string portPath,
        int baudRate,
        SerialWorkerType type,
        IDeviceRegistry registry)
    {

        _logger.LogDebug(
            "Creating serial worker {WorkerType} for {PortPath} at {BaudRate}",
            type,
            portPath,
            baudRate);

        return type switch
        {
            SerialWorkerType.Text => new SerialPortWorker(
                portPath, baudRate,
                _loggerFactory.CreateLogger<SerialPortWorker>(),
                registry),

            // Add new worker types here — they all receive registry so they
            // can annotate outbound messages with function names.
            SerialWorkerType.Flir => new FlirSerialWorker(
                portPath, baudRate,
                _loggerFactory.CreateLogger<FlirSerialWorker>(),
                registry),

            _ => throw new ArgumentOutOfRangeException(nameof(type), type, null)
        };
    }
}

// ═════════════════════════════════════════════════════════════════════════════
// DI extension
// ═════════════════════════════════════════════════════════════════════════════

public static class SerialPortManagerServiceExtensions
{
    /// <summary>
    /// Register all serial port manager services with the DI container.
    /// </summary>
    public static IServiceCollection AddSerialPortManager(
      this IServiceCollection services, IConfiguration configuration)
    {
        services.AddSingleton<IDeviceRegistry, DeviceRegistry>();
        services.AddSingleton<ISerialWorkerFactory, SerialWorkerFactory>();
        services.AddSingleton<IUsbDeviceEnumerator>(sp =>
            UsbDeviceEnumeratorFactory.Create(sp.GetRequiredService<ILoggerFactory>()));
        services.AddSingleton<SerialPortManagerService>();
        services.AddSingleton<ISerialPortManagerService>(
            sp => sp.GetRequiredService<SerialPortManagerService>());
        services.AddHostedService(
            sp => sp.GetRequiredService<SerialPortManagerService>());
        services.AddSingleton<ISerialCommandDispatcher, SerialCommandDispatcher>();
        services.AddSingleton<SerialPushPump>();
        services.AddHostedService(sp => sp.GetRequiredService<SerialPushPump>());
        services.AddSingleton<TcpSerialCommandHandler>();
        services.AddHostedService<TcpHostService>();

        services.Configure<GpioUartOptions>(configuration.GetSection(GpioUartOptions.SectionName));
        services.AddHostedService<GpioUartStartupService>();

        return services;
    }
}
