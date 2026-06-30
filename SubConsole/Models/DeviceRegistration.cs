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
        public int BaudRate { get; set; }
        public SerialWorkerType SerialWorkerType { get; }
        public string? CurrentPortPath { get; internal set; }

        public SerialPortSettings? PortSettings { get; init; }

        public DeviceRegistration(
            UsbSerialPortInfo identifier, string functionName, int baudRate, SerialWorkerType serialWorker)
        {
            Identifier = identifier;
            FunctionName = functionName;
            SerialWorkerType = serialWorker;
            BaudRate = baudRate;
            Key = identifier.Key;

            // Static (non-USB) devices have a fixed, known port path at
            // construction time — seed CurrentPortPath immediately so the
            // very first persist (before SetPortPath is called) doesn't
            // write a null/empty value.
            CurrentPortPath = identifier.IsStatic ? identifier.PortName : null;
        }
    }
}