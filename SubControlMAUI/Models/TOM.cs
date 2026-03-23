using System;
using System.Collections.Generic;
using System.Security.Principal;
using System.Text;

namespace SubConsole.Models
{

    //// Turn on TOM
    //await HandleCommand("COM5", "SEND", $"$PBLUTP,S,PWR,CTRL,ON,15*29");
    public static class TOM
    {
        public static string CommandPort { get; set; } = "COM5";

        public static string TurnOnAllSystemsCommand { get; set; } = $"$PBLUTP,S,PWR,CTRL,ON,15*29";

        public static string TurnOffCommand { get; set; }


    }
}
