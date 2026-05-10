using System;
using System.Collections.Generic;
using System.Text;

namespace SubControlMAUI.Models
{
    public class RegisteredCameraEntry
    {
        public string DeviceId { get; set; } = "";
        public string? FriendlyName { get; set; }
        public string? StreamPathName { get; set; }
        public bool IsRegisteredWithMtx { get; set; }
    }
}
