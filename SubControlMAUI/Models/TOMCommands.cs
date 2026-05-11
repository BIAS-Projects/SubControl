using System;
using System.Collections.Generic;
using System.Security.Principal;
using System.Text;

namespace SubConsole.Models
{

    //// Turn on TOM
    //await HandleCommand("COM5", "SEND", $"$PBLUTP,S,PWR,CTRL,ON,15*29");
    public static class TOMCommands
    {
        public static string OpenCommand { get; set; } = "OPEN";
        public static string WriteCommand { get; set; } = "WRITE TEXT";

        public static string CloseCommand { get; set; } = "CLOSE";

        //public static string CommandPort { get; set; } = "COM5";

        public static int TomBaudCommandBaudRate { get; set; } = 115200;

        public static string TurnOnAllSystemsCommand { get; set; } = $"$PBLUTP,S,PWR,CTRL,ON,15*29";

        public static string TurnOnAllSystemsResponse { get; set; } = $"$PBLUTP,R,PWR,CTRL,ON,15*29";

        public static string TurnOffAllSystemsCommand { get; set; } = $"$PBLUTP,S,PWR,CTRL,OFF,15*67";

        public static string TurnOffAllSystemsResponse { get; set; } = $"$PBLUTP,R,PWR,CTRL,OFF,15*67";

        public static string GetStatusCommand { get; set; } = $"$PBLUTP,Q,SYS,INFO*25";

        public static string TurnOffCommand { get; set; }


    }
}
