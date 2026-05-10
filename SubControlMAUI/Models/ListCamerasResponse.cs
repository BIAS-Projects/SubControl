using System;
using System.Collections.Generic;
using System.Text;

namespace SubControlMAUI.Models
{
    public class ListCamerasResponse
    {
        public bool Ok { get; set; }
        public string? Error { get; set; }
        public List<CameraInfo> Data { get; set; } = new();
    }
}
