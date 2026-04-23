using SubConsole.Models;
using System;
using System.Collections.Generic;
using System.Management;
using System.Text;
using System.Text.RegularExpressions;

namespace SubConsole.Helpers
{
    public static class SerialPortHelper
    {
        private static readonly Regex ComRegex = new(@"\(COM(\d+)\)", RegexOptions.IgnoreCase);
        private static readonly Regex VidRegex = new(@"VID_([0-9A-F]{4})", RegexOptions.IgnoreCase);
        private static readonly Regex PidRegex = new(@"PID_([0-9A-F]{4})", RegexOptions.IgnoreCase);

        public static List<SerialDeviceInfo> GetSerialDevices()
        {
            var results = new List<SerialDeviceInfo>();

            using var searcher = new ManagementObjectSearcher(
                "SELECT * FROM Win32_PnPEntity WHERE Name LIKE '%(COM%'");

            foreach (ManagementObject obj in searcher.Get())
            {
                var name = obj["Name"]?.ToString() ?? "";
                var pnpId = obj["PNPDeviceID"]?.ToString() ?? "";

                var comMatch = ComRegex.Match(name);
                if (!comMatch.Success)
                    continue;

                var comPort = "COM" + comMatch.Groups[1].Value;

                var vidMatch = VidRegex.Match(pnpId);
                var pidMatch = PidRegex.Match(pnpId);

                string? serial = ExtractSerial(pnpId);

                results.Add(new SerialDeviceInfo
                {
                    ComPort = comPort,
                    Name = name,
                    PnpDeviceId = pnpId,
                    Vid = vidMatch.Success ? vidMatch.Groups[1].Value : null,
                    Pid = pidMatch.Success ? pidMatch.Groups[1].Value : null,
                    Serial = serial
                });
            }

            return results;
        }

        private static string? ExtractSerial(string pnpId)
        {
            // Example formats:
            // USB\VID_0403&PID_6001\A50285BI
            // USB\VID_1A86&PID_7523\5&1A2B3C4D&0&3

            var parts = pnpId.Split('\\');

            if (parts.Length < 3)
                return null;

            var lastPart = parts[2];

            // Heuristic: if it contains '&' it's probably location-based, not a true serial
            if (lastPart.Contains('&'))
                return null;

            return lastPart;
        }
    }

}
