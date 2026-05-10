using System;
using System.Collections.Generic;
using System.Text;

namespace SubControlMAUI.Models
{
    public class ListRegisteredCamerasResponse
    {
        public bool Ok { get; set; }
        public string? Error { get; set; }
        public List<RegisteredCameraEntry> Data { get; set; } = new();
    }

}
