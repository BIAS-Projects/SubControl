// ================================================================
//  CameraRegistrationEntity.cs
// ================================================================

using SQLite;
using SubConsole.Models;
using SubConsole.Services.Video;
using SubConsole.Services.Helpers;
using System.ComponentModel.DataAnnotations;
using System.Text.Json;

namespace SubConsole.Models
{
    public class CameraRegistrationEntity
    {
        /// <summary>Stable device ID — sysfs path (Linux) or registry path (Windows).</summary>
        [PrimaryKey]
        public string DeviceId { get; set; } = "";

        /// <summary>MediaMTX path name, e.g. "usbcamera" or "flir".</summary>
        [Required]
        public string StreamPathName { get; set; } = "";

        /// <summary>UsbCameraInfo serialised as JSON.</summary>
        [Required]
        public string CameraJson { get; set; } = "";

        /// <summary>FfmpegCameraOptions serialised as JSON.</summary>
        [Required]
        public string FfmpegOptionsJson { get; set; } = "";

        /// <summary>MediaMtxPathConfig serialised as JSON.</summary>
        [Required]
        public string MtxConfigJson { get; set; } = "";

        /// <summary>Whether this path has been successfully pushed to MediaMTX.</summary>
        public bool IsRegisteredWithMtx { get; set; }

        // ── Mapping helpers ───────────────────────────────────────────────────

        public static CameraRegistrationEntity ToEntity(CameraRegistration model)
        {
            return new CameraRegistrationEntity
            {
                DeviceId = model.Camera.DeviceId,
                StreamPathName = model.StreamPathName,
                CameraJson = JsonSerializer.Serialize(model.Camera),
                FfmpegOptionsJson = JsonSerializer.Serialize(model.FfmpegOptions),
                MtxConfigJson = JsonSerializer.Serialize(model.MtxConfig),
                IsRegisteredWithMtx = model.IsRegisteredWithMtx
            };
        }

        public static CameraRegistration ToModel(CameraRegistrationEntity entity)
        {
            var camera = JsonSerializer.Deserialize<UsbCameraInfo>(entity.CameraJson)
                ?? throw new InvalidOperationException(
                    $"Failed to deserialise CameraJson for device {entity.DeviceId}");

            var ffmpegOptions = JsonSerializer.Deserialize<FfmpegCameraOptions>(entity.FfmpegOptionsJson)
                ?? throw new InvalidOperationException(
                    $"Failed to deserialise FfmpegOptionsJson for device {entity.DeviceId}");

            var mtxConfig = JsonSerializer.Deserialize<MediaMtxPathConfig>(entity.MtxConfigJson)
                ?? throw new InvalidOperationException(
                    $"Failed to deserialise MtxConfigJson for device {entity.DeviceId}");

            return new CameraRegistration(camera, entity.StreamPathName, ffmpegOptions, mtxConfig)
            {
                IsRegisteredWithMtx = entity.IsRegisteredWithMtx
            };
        }
    }
}