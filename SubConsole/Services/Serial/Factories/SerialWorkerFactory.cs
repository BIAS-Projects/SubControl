using Microsoft.Extensions.Logging;
using SubConsole.Serial;
using SubConsole.Services.Serial.Workers;
using System;
using System.Collections.Generic;
using System.Text;
using static SubConsole.Services.TcpHostService;

namespace SubConsole.Services.Serial.Factories
{
    public interface ISerialWorkerFactory
    {
        ISerialWorker Create(string port, int baudRate, SerialWorkerType type);
    }

    public class SerialWorkerFactory : ISerialWorkerFactory
    {
        private readonly ILogger<SerialWorkerFactory> _logger;

        public SerialWorkerFactory(ILogger<SerialWorkerFactory> logger)
        {
            _logger = logger;
        }

        public ISerialWorker Create(string port, int baudRate, SerialWorkerType type)
        {
            return type switch
            {
                SerialWorkerType.Text => new SerialPortWorker(port, baudRate, _logger),
                SerialWorkerType.Flir => new FlirCameraClient(port, baudRate),
                _ => throw new ArgumentException($"Unknown worker type: {type}")
            };
        }
    }

}
