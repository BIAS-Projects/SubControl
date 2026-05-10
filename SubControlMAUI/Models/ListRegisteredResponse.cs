using System;
using System.Collections.Generic;
using System.Text;

namespace SubControlMAUI.Models
{
    public class ListRegisteredResponse
    {
        public bool Ok { get; set; }
        public string? Error { get; set; }
        public List<RegisteredPortEntry> Data { get; set; } = new();
    }
}
