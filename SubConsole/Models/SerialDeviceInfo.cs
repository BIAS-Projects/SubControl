using System;
using System.Collections.Generic;
using System.Text;

namespace SubConsole.Models
{
    public class SerialDeviceInfo
    {
        public string ComPort { get; set; } = "";
        public string Name { get; set; } = "";
        public string PnpDeviceId { get; set; } = "";
        public string? Vid { get; set; }
        public string? Pid { get; set; }
        public string? Serial { get; set; }
    }
}
