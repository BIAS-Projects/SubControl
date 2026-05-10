using System;
using System.Collections.Generic;
using System.Text;

namespace SubControlMAUI.Models
{
    public class RegisteredPortEntry
    {
        public string Key { get; set; } = "";
        public string? Description { get; set; }
        public string? FunctionName { get; set; }
        public string? CurrentPort { get; set; }
    }
}
