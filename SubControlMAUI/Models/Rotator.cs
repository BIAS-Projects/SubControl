
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

        public static string ReportedForwardLimit { get; set; } = "";

        public static string ReportedBackwardLimit { get; set; } = "";

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

        public static string GetFirmwareVersion { get; set; } = $"#AMRV0000R\r\n";

        public static string FactoryReset { get; set; } = $"#AMFR0000W\r\n";

        public static string MotorPositionResetToZero { get; set; } = $"#AMPR5000W\r\n";

        public static string SetForwardLimitTo360 { get; set; } = $"#AMLF9095W\r\n";

        public static string SetBackwardLimitTo360 { get; set; } = $"#AMLF0905W\r\n";

        public static string GetForwardLimit { get; set; } = $"#AMRF0000R\r\n";

        public static string GetBackwardLimit { get; set; } = $"#AMRB0000R\r\n";


        public static string ConvertDegreesToCommandValue (int degrees)
        {
            //Command value = Degrees / Scale Factor of Encoder + 5000
            //Encode scale factor = 0.879
            //0 will always be 5000
            //112/0.879 + 5000 = 6274
            //180 / 0.0879 + 5000 = 7,047.78156996587 


            var value = degrees / _encoderScalingFactor + 5000;
            return ((int)Math.Round(value)).ToString("D4");
        }


        public static string GenerateParkOrDeployCommandString(bool IsPark)
        {
            string prefix = $"#AMML";
            string postfix = $"W\\r\\n";
            string command = "";
            if (IsPark)
            {
   
                command = prefix + ConvertDegreesToCommandValue(MinRotatorValue) + postfix;
            }
            else
            {
                command = prefix + ConvertDegreesToCommandValue(MaxRotatorValue) + postfix;
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
              //  string targetPositionText = targetPosition.ToString().PadLeft(4, '0');

                command = prefix + ConvertDegreesToCommandValue(targetPosition) + postfix;

              //  command = prefix + targetPositionText + postfix;
            }
            else
            {
                int targetPosition = armAngleInt + AdjustValue;
                if (targetPosition > MaxRotatorValue)
                {
                    targetPosition = MaxRotatorValue;
                }
                command = prefix + ConvertDegreesToCommandValue(targetPosition) + postfix;

            //    string targetPositionText = targetPosition.ToString().PadLeft(4, '0');
            //    command = prefix + targetPositionText + postfix;
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


        public static string ReturnCommandResponseAsDegrees(string response)
        {

                response = response.Trim();

                if (response.Length < 10)
                {
                    return string.Empty;
                }

                if (!int.TryParse(response.Substring(5, 4), out int encoder))
                {
                    return string.Empty;
                }

                double degrees = (encoder - 5000) * 0.0879;
                return Math.Round(degrees).ToString();

            

        }
    }
}
