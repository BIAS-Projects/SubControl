using SQLite;
using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.Linq;
using System.Reflection.PortableExecutable;
using System.Text;
using System.Threading.Tasks;
using System.Device.I2c;

namespace SubControl.Model
{
    public class Config
    {
        [PrimaryKey]
        public int Id { get; set; } = 0;
        [Required]
        public string IPAddress { get; set; } = "192.168.0.1";
        [Required]
        public string Port { get; set; } = "8080";
        [Required]
        public string Rs232Port { get; set; } = "COM1";
        [Required]
        public int BaudRate { get; set; } = 9600;
        [Required]
        public string Parity { get; set; } = "None";
        [Required]
        public int DataBits { get; set; } = 8;
        [Required]
        public string StopBits { get; set; } = "None";
        [Required]
        public string FlowControl { get; set; } = "None";
        [Required]
        public int BusId { get; set; } = 1;
        [Required]
        public int DeviceAddress { get; set; } = 60;
        [Required]
        public int ClockRate { get; set; } = 100_000;

        [Required]
        public string CutterUpCommand { get; set; } = "CUTTER_UP";
        [Required]
        public string CutterDownCommand { get; set; } = "CUTTER_DOWN";
        [Required]
        public string CutterLeftCommand { get; set; } = "CUTTER_LEFT";
        [Required]
        public string CutterRightCommand { get; set; } = "CUTTER_RIGHT";

        [Required]
        public string PeriscopeUpCommand { get; set; } = "PERISCOPE_UP";

        [Required]
        public string PeriscopeDownCommand { get; set; } = "PERISCOPE_DOWN";

        [Required]
        public string Features { get; set; } = "CUTTER,PERISCOPE";

    }
}
