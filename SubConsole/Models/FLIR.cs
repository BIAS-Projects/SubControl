using System;
using System.Collections.Generic;
using System.Text;

namespace SubConsole.Models
{
    public static class FLIR
    {
        public static string CommandPort { get; set; } = "COM10";

        public static int FLIRBaudCommandBaudRate { get; set; } = 921600;

        public static string LUTRainBow { get; set; } = "LUTRainbow";

        public static string LUTWhiteHot { get; set; } = "LUTWhiteHot";

        //public static string TurnOnAllSystemsCommand { get; set; } = $"$PBLUTP,S,PWR,CTRL,ON,15*29";

        //public static string TurnOffAllSystemsCommand { get; set; } = $"$PBLUTP,S,PWR,CTRL,OFF,15*67";

        //public static string GetStatusCommand { get; set; } = $"$PBLUTP,Q,SYS,INFO*25";

        //public static string TurnOffCommand { get; set; }


    }
}
