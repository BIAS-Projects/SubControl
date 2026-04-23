using System;
using System.Collections.Generic;
using System.Text;

namespace SubControlMAUI.Models
{
    public static class SystemCommands
    {
        public static string ListDevicesCommand { get; } = "LIST DEVICES";

        public static string ListRegisterDevicesCommand { get;} = "LIST REGISTERED";

        public static string RegisterDeviceCommand { get; } = "REGISTER";

        public static string UnregisterDeviceCommand { get; } = "UNREGISTER";

        public static string AssignPortCommand { get; } = "ASSIGN PORT";



    }
}
