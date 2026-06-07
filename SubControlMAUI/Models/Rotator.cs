
using System;
using System.Collections.Generic;
using System.Text;

namespace SubControlMAUI.Models
{
    public static class Rotator
    {

        public static int MinRotatorValue { get; set; } = 0;


        public static int MaxRotatorValue { get; set; } = 0;


        public static int AdjustValue { get; set; } = 0;

        public static int Speed { get; set; } = 40;

        public static string Version { get; set; } = "";

        public static int BackwardLimit { get; set; } = 0;

        public static int ForwardLimit { get; set; } = 0;

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


        public static string GenerateParkOrDeployCommandString(bool IsPark)
        {
            string prefix = $"#AMML";
            string postfix = $"W\\r\\n";
            string command = "";
            if (IsPark)
            {
                string minValueText = MinRotatorValue.ToString().PadLeft(4, '0');
                command = prefix + minValueText + postfix;
            }
            else
            {
                string maxValueText = MaxRotatorValue.ToString().PadLeft(4, '0');
                command = prefix + maxValueText + postfix;
            }
            return command;
        }

        public static string GenerateNudgeCommandString(bool isBackWards, double armAngle)
        {
            string prefix = $"#AMML";
            string postfix = $"W\\r\\n";
            int armAngleInt = (int)Math.Round(armAngle);
            string command = "";
            if (isBackWards)
            {
                int targetPosition = armAngleInt - AdjustValue;
                if(targetPosition < MinRotatorValue)
                {
                    targetPosition = MinRotatorValue;
                }
                string targetPositionText = targetPosition.ToString().PadLeft(4, '0');
                command = prefix + targetPositionText + postfix;
            }
            else
            {
                int targetPosition = armAngleInt + AdjustValue;
                if (targetPosition > MaxRotatorValue)
                {
                    targetPosition = MaxRotatorValue;
                }
                string targetPositionText = targetPosition.ToString().PadLeft(4, '0');
                command = prefix + targetPositionText + postfix;
            }
            return command;
        }

        public static string GenerateSetSpeedCommandString()
        {

        //Valid speeds are 1 – 40, representing 0.5 to 20 degrees / second, with //0.5 degrees / second increments.
        //The default speed is 16, which is 8 degrees / second.All of these values are valid with the standard gear set of 88:1.

            string prefix = $"#AMSP";
            string postfix = $"W\\r\\n";
            string command = "";

            string speedText = Speed.ToString().PadLeft(4, '0');
            command = prefix + speedText + postfix;
            return command;
        }
    }
}
