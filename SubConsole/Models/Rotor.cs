using System;
using System.Collections.Generic;
using System.Text;

namespace SubConsole.Models
{
    public static class Rotor
    {
        public static string CommandPort { get; set; } = "COM11";

        public static int TomBaudCommandBaudRate { get; set; } = 9600;

        public static string PanMotorAForward  { get; set; } = $"#AMMF0000W\r\n";

        public static string PanMotorABackward { get; set; } = $"#AMMB0000W\r\n";

        public static string StopPanMotorA { get; set; } = $"#AMST0000W\r\n";



    }
}
