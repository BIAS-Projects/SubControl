



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

        public static string BrakeOn { get; set; } = $"#AMBS0000W\r\n";

        public static string BrakeOff { get; set; } = $"#AMBR0000W\r\n";
        public static string MotorPositionResetToZero { get; set; } = $"#AMPR5000W\r\n";

        public static string SetForwardLimitTo360 { get; set; } = $"#AMLF9095W\r\n";

        public static string SetBackwardLimitTo360 { get; set; } = $"#AMLF0905W\r\n";

        public static string GetForwardLimit { get; set; } = $"#AMRF0000R\r\n";

        public static string GetBackwardLimit { get; set; } = $"#AMRB0000R\r\n";

        public static string GetSpeedSetting { get; set; } = $"#AMRS0000R\r\n";

        public static string GetBrakeSetting { get; set; } = $"#AMRK0000R\r\n";

        public static string GetBrakePower { get; set; } = $"#AMRP0000R\r\n";

        public static string GetMotorDriveCurrent { get; set; } = $"#AMRC0000R\r\n";

        public static string GetMotorStepType { get; set; } = $"#AMRM0000R\r\n";

        public static string GetMotorTemp { get; set; } = $"#ATMP0000R\r\n";

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

        public static string GenerateSetMotorCurrentCommand(int percentage)
        {
            if (percentage > 0 || percentage < 100)
            {
                return string.Empty;
            }
            string prefix = $"#AMMC";
            string postfix = $"W\\r\\n";
            string command = percentage.ToString("D4");
            command = prefix + command + postfix;
            return command;


        }

        public static string GenerateMotorSpeedCommand(int speed)
        {
            if (speed > 1 || speed < 40)
            {
                return string.Empty;
            }
            string prefix = $"#AMSP";
            string postfix = $"W\\r\\n";
            string command = speed.ToString("D4");
            command = prefix + command + postfix;
            return command;


        }

        public static string GenerateMotorLimitCommand(bool isBackwardLimit, int limitInDegrees)
        {
            if (limitInDegrees < -360 || limitInDegrees > 360)
            {
                return String.Empty;
            }
            string prefix = $"#AMLF";
            if (isBackwardLimit)
            {
                prefix = $"#AMLB";
            }


            string postfix = $"W\\r\\n";
            string command = ConvertDegreesToCommandValue(limitInDegrees);
            command = prefix + command + postfix;
            return command;


        }

        public static string GenerateMotorBrakeCommand(bool brakeOn)
        {
            if(brakeOn)
            {
                return Rotator.BrakeOn;
            }
            else
            {
                return Rotator.BrakeOff;
            }


        }

        public static string GenerateSetMotorBrakePowerCommand(int percentage)
        {
            if (percentage > 0 || percentage < 100)
            {
                return string.Empty;
            }
            string prefix = $"#AMBP";
            string postfix = $"W\\r\\n";
            string command = percentage.ToString("D4");
            command = prefix + command + postfix;
            return command;


        }


        public static string GenerateWriteEepromRegisterCommand(string data)
        {

            if(data.Length != 4)
            {
                return String.Empty;
            }

            string prefix = $"#AMEE";
            string postfix = $"W\\r\\n";
            string command = prefix + data + postfix;
            return command;


        }

        public static string GenerateSetMotorStepTypeCommand(int stepType)
        {
            //step types as follows: Wave - 0, Full - 1,  Half - 2, Sine - 3.The default setting is sine.

            if (stepType < 0 || stepType > 3)
            {
                return String.Empty;
            }

            string prefix = $"#AMMS";
            string postfix = $"W\\r\\n";
            string command = stepType.ToString("D4");
            command = prefix + command + postfix;
            return command;


        }


//        Useful MRE Addresses
//The MRE command reads back a byte from the register address(0 – 255) indicated in the command’s data
//bytes.These include the program variables.This can be useful in diagnostics.All bytes of 31 and below
//are Special Function Registers, as are the bytes 128 – 159. EEPROM bytes of interest include:
//DEVICEADDRESS at address 001
//MOTORSPEED setting at address 002.
//FLAGS1 at address 011. The MODEFLAG in bit 0 selects RS232 or RS485 communication protocol (1 =
//RS485). The BRAKEFLAG in pit 1 sets the braking on(1) and off(0).
//COMMANDRECORDS at address 016 – 239. 7 bytes for each record are stored in a FIFO arrangement.
//It stores 32 records of the most recent ‘W’ commands, starting at the COMMANDPOINTER and wrapping
//around.
//COMMANDPOINTER at address 0240 points to the beginning of the next 7 byte stored command record.
//See COMMANDRECORDS

        public static string GenerateReadEepromLocationCommand(int location)
        {

            if (location < 0 || location > 255)
            {
                return String.Empty;
            }

            string prefix = $"#AMRE";
            string postfix = $"W\\r\\n";
            string command = prefix + location.ToString("D4") + postfix;
            return command;


        }


        public static string GenerateReadRAMLocationCommand(int location)
        {

            if (location < 0 || location > 9999)
            {
                return String.Empty;
            }

            string prefix = $"#AMRR";
            string postfix = $"W\\r\\n";
            string command = prefix + location.ToString("D4") + postfix;
            return command;


        }

    }
}
