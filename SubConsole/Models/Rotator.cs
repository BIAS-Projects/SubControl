using Microsoft.VisualBasic;
using System;
using System.Collections.Generic;
using System.Text;

namespace SubConsole.Models
{
    public static class Rotator
    {

        public static string[] Terminators = {"R", "W" }; // lower case until the motor is "homed"

        public static string[] Headers = {"#", "$","&","%", "!" };

        public static string Node = "A";

        public static string[] Commands = {"MMF", "MMB", "MMC", "MML", "MST", "MSP", "MLF", "MLB", "MBS", "MBR", "MBP", "MEE", "MSS", "MFR", "MPR",
        "MRL", "MRS", "MRB", "MRF", "MRK", "MRA", "MRV", "MRE", "MRR", "MRP", "MRC", "MRM", "TMP"};

        public static string CommandPort { get; set; } = "COM11";

        public static int TomBaudCommandBaudRate { get; set; } = 9600;

        public static string PanMotorAForward  { get; set; } = $"#AMMF0000W\r\n";

        public static string PanMotorABackward { get; set; } = $"#AMMB0000W\r\n";

        public static string StopPanMotorA { get; set; } = $"#AMST0000W\r\n";



    }
}
