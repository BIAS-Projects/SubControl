using System;
using System.Collections.Generic;
using System.Text;
using static SubConsole.Models.UsbDeviceInfo;

namespace SubConsole.Models
{
    public sealed class DeviceRegistration
    {
        public UsbSerialPortInfo Identifier { get; }
        public string Key { get; }
        public string FunctionName { get; }
        public SerialWorkerType SerialWorkerType { get; }
        public string? CurrentPortPath { get; internal set; }
        public SerialPortSettings PortSettings { get; }

        public int BaudRate => PortSettings.BaudRate;   // kept for the DB column / callers

        public DeviceRegistration(
            UsbSerialPortInfo identifier, string functionName,
            SerialWorkerType serialWorker, SerialPortSettings portSettings)
        {
            Identifier = identifier;
            FunctionName = functionName;
            SerialWorkerType = serialWorker;
            PortSettings = portSettings;
            Key = identifier.Key;
            CurrentPortPath = identifier.IsStatic ? identifier.PortName : null;
        }
    }
}