using System;
using System.Collections.Generic;
using System.Text;
using System.ComponentModel.DataAnnotations;
namespace SubConsole.Models
{
    using global::System.Text.Json;
    using SQLite;
    using static SubConsole.Models.UsbDeviceInfo;
    public class DeviceRegistrationEntity
    {
        [PrimaryKey]
        public string Key { get; set; }
        [Required]
        public string FunctionName { get; set; }
        [Required]
        public int BaudRate { get; set; }
        [Required]
        public int SerialWorkerType { get; set; }
        [Required]
        public string IdentifierJson { get; set; }
        [Required]
        public string CurrentPortPath { get; set; }
        public string? PortSettingsJson
        {
            get; set;
        }
        public static DeviceRegistrationEntity ToEntity(DeviceRegistration model)
        {
            return new DeviceRegistrationEntity
            {
                Key = model.Key,
                FunctionName = model.FunctionName,
                BaudRate = model.PortSettings.BaudRate,   // legacy column kept in sync
                SerialWorkerType = (int)model.SerialWorkerType,
                IdentifierJson = JsonSerializer.Serialize(model.Identifier),
                CurrentPortPath = model.CurrentPortPath,
                PortSettingsJson = JsonSerializer.Serialize(model.PortSettings)
            };
        }
        public static DeviceRegistration ToModel(DeviceRegistrationEntity entity)
        {
            var identifier = JsonSerializer.Deserialize<UsbSerialPortInfo>(entity.IdentifierJson)
                ?? throw new InvalidOperationException("Failed to deserialize Identifier");

            // Rows saved before PortSettings existed fall back to the old BaudRate column.
            var legacy = SerialPortSettings.Default with { BaudRate = entity.BaudRate };
            var portSettings = string.IsNullOrWhiteSpace(entity.PortSettingsJson)
                ? legacy
                : JsonSerializer.Deserialize<SerialPortSettings>(entity.PortSettingsJson) ?? legacy;

            return new DeviceRegistration(
                identifier,
                entity.FunctionName,
                (SerialWorkerType)entity.SerialWorkerType,
                portSettings)
            {
                CurrentPortPath = entity.CurrentPortPath
            };
        }
    }
}