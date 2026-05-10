
using System;
using System.Collections.Generic;
using System.Text;

namespace SubControlMAUI.Models
{
    public class UsbDeviceInfo
    {

        public enum SerialWorkerType
        {
            Text,
            Flir
        }

        public string DeviceId { get; set; } = "";
        public string VendorId { get; set; } = "";
        public string ProductId { get; set; } = "";
        public string Description { get; set; } = "";
    }
}
