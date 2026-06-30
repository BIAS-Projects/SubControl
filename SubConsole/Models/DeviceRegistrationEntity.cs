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
                BaudRate = model.BaudRate,
                SerialWorkerType = (int)model.SerialWorkerType,
                IdentifierJson = JsonSerializer.Serialize(model.Identifier),
                CurrentPortPath = model.CurrentPortPath,
                PortSettingsJson = model.PortSettings is null
                    ? null
                    : JsonSerializer.Serialize(model.PortSettings)
            };
        }
        public static DeviceRegistration ToModel(DeviceRegistrationEntity entity)
        {
            var identifier = JsonSerializer.Deserialize<UsbSerialPortInfo>(entity.IdentifierJson);

            var portSettings = string.IsNullOrWhiteSpace(entity.PortSettingsJson)
                ? null
                : JsonSerializer.Deserialize<SerialPortSettings>(entity.PortSettingsJson);

            return new DeviceRegistration(
                identifier,
                entity.FunctionName,
                entity.BaudRate,
                (SerialWorkerType)entity.SerialWorkerType
            )
            {
                CurrentPortPath = entity.CurrentPortPath,
                PortSettings = portSettings
            };
        }
    }
}