using System;
using System.Collections.Generic;
using System.Text;
using static SubConsole.Models.UsbDeviceInfo;

namespace SubConsole.Models
{
    /// <summary>
    /// Associates a <see cref="DeviceIdentifier"/> with one or more logical
    /// function names (e.g. "FLIR_CAMERA", "TOM_CONTROLLER").
    /// </summary>
    public sealed class DeviceRegistration
    {
        // public DeviceIdentifier Identifier { get; }

        public UsbSerialPortInfo Identifier { get; }

        /// <summary>Stable registry key, built at registration time with the known port path.</summary>
        public string Key { get; }

        public string FunctionName { get; }

        public int BaudRate { get; set; }

        public SerialWorkerType SerialWorkerType { get; }

        /// <summary>Current OS port path (e.g. "COM3" or "/dev/ttyUSB0").</summary>
        public string? CurrentPortPath { get; internal set; }

        //public DeviceRegistration(
        //    DeviceIdentifier identifier, string functionName, int baudRate, SerialWorkerType serialWorker)
        //{
        //    Identifier = identifier;
        //    FunctionName = functionName;
        //    SerialWorkerType = serialWorker;
        //    BaudRate = baudRate;
        //    //Key = identifier.BuildKey(portPath);
        //}

        public DeviceRegistration(
        UsbSerialPortInfo identifier, string functionName, int baudRate, SerialWorkerType serialWorker)
        {
            Identifier = identifier;
            FunctionName = functionName;
            SerialWorkerType = serialWorker;
            BaudRate = baudRate;
            Key = identifier.Key;
        }
    }
}
