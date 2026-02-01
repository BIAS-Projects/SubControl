using SQLite;
using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.Linq;
using System.Reflection.PortableExecutable;
using System.Text;
using System.Threading.Tasks;

namespace SubControlMAUI.Model
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
        public string UpCommand { get; set; } = "Up";
        [Required]
        public string DownCommand { get; set; } = "Down";
        [Required]
        public string LeftCommand { get; set; } = "Left";
        [Required]
        public string RightCommand { get; set; } = "Right";



    }
}
