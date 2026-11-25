using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.Security.Principal;
using System.Text;

namespace SubControlMAUI.Models;

public record FunctionToPortEntry
{
    public string DeviceKey { get; set; }
    public string FunctionName { get; set; }
    public SerialPortSettings Settings { get; set; } = SerialPortSettings.Default;
    public string  WorkerType { get; set; }
}
