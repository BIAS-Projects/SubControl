using System;
using System.Collections.Generic;
using System.Text;

namespace SubConsole.Models
{
    public record DeviceKeyToPortPathEntry
    {
        public string DeviceKey { get; set; }
        public string PortPath { get; set; }
    }
}
