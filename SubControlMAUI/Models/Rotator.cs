using System;
using System.Collections.Generic;
using System.Text;

namespace SubControlMAUI.Models
{
    public static class Rotator
    {
        private static double _encoderScalingFactor = 0.0879;

        public static readonly HashSet<string> Headers =
            new(StringComparer.OrdinalIgnoreCase)
            { "#", "$", "&", "%", "!" };

        public static readonly HashSet<string> Terminators =
            new(StringComparer.OrdinalIgnoreCase)
            { "R", "W" };

        public static readonly HashSet<string> Commands =
            new(StringComparer.OrdinalIgnoreCase)
            {
        "MMF", "MMB", "MMC", "MML", "MST", "MSP",
        "MLF", "MLB", "MBS", "MBR", "MBP",
        "MEE", "MSS", "MFR", "MPR",
        "MRL", "MRS", "MRB", "MRF",
        "MRK", "MRA", "MRV", "MRE",
        "MRR", "MRP", "MRC", "MRM",
        "TMP"
            };
        public static string PanMotorAForward  { get; set; } = $"#AMMF0000W\r\n";

        public static string PanMotorABackward { get; set; } = $"#AMMB0000W\r\n";

        public static string ParkMotorA { get; set; } = $"#AMML5000W\r\n";

        public static string DeployMotorA { get; set; } = $"#AMML7100W\r\n";
       // public static string DeployMotorA { get; set; } = $"#AMML6274W\r\n";

        public static string StopPanMotorA { get; set; } = $"#AMST0000W\r\n";

  //      public static string StopPanMotorA { get; set; } = $"#AMLF7101W\r\n";

        public static string EncoderLocationA { get; set; } = $"#AMRL0000R\r\n";

        public static string GetFirmwareVersion { get; set; } = $"##AMRV0000R\r\n";


        public static int ConvertDegreesToCommandValue (int degrees)
        {
            //Command value = Degrees / Scale Factor of Encoder + 5000
            //Encode scale factor = 0.879
            //0 will always be 5000
            //112/0.879 + 5000 = 6274
            //180 / 0.0879 + 5000 = 7,047.78156996587 


            if (degrees < 0 || degrees > 360)
            {
                return -1;
            }
            var value = degrees / _encoderScalingFactor + 5000;
            return (int)Math.Round(value);
        }

    }
}
