using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SubConsole.Models;
using System.IO.Ports;
using static SubConsole.Models.UsbDeviceInfo;

namespace SubConsole.Services.Serial;

public sealed class GpioUartStartupService : BackgroundService
{
    private readonly ISerialPortManagerService _manager;
    private readonly GpioUartOptions _options;
    private readonly ILogger<GpioUartStartupService> _logger;

    public GpioUartStartupService(
        ISerialPortManagerService manager,
        IOptions<GpioUartOptions> options,
        ILogger<GpioUartStartupService> logger)
    {
        _manager = manager;
        _options = options.Value;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.Enabled) return;

        // Let SerialPortManagerService finish loading the DB-backed registry first
        await Task.Delay(TimeSpan.FromSeconds(1), stoppingToken);

        var settings = new SerialPortSettings(
            DataBits: _options.DataBits,
            Parity: Enum.Parse<Parity>(_options.Parity, ignoreCase: true),
            StopBits: Enum.Parse<StopBits>(_options.StopBits, ignoreCase: true),
            Handshake: Enum.Parse<Handshake>(_options.Handshake, ignoreCase: true));

        var registerResult = await _manager.RegisterStaticDeviceAsync(
            deviceId: "GPIO-UART0",
            portPath: _options.PortPath,
            functionName: _options.FunctionName,
            baudRate: _options.BaudRate,
            type: SerialWorkerType.Text,
            portSettings: settings,
            token: stoppingToken);

        if (!registerResult.IsSuccess)
        {
            _logger.LogError("Failed to register GPIO UART: {Message}", registerResult.Message);
            return;
        }

        if (_options.AutoOpen)
        {
            var openResult = await _manager.OpenPortAsync(_options.FunctionName, stoppingToken);
            _logger.LogInformation(
                "GPIO UART auto-open: {Success} ({Message})",
                openResult.IsSuccess, openResult.Message);
        }
    }
}