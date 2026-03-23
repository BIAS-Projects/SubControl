using SQLite;

namespace SubControl.Model
{
    /// <summary>
    /// Persists the optimal streaming caps for a camera so that on subsequent
    /// startups the probe phase is skipped entirely.
    ///
    /// The composite key is (DeviceName, ProviderType) — the same physical camera
    /// may be accessible via both KS and MF on Windows and each provider requires
    /// different caps.  Storing them separately prevents MF caps being used when
    /// the camera arrives via KS and vice-versa.
    /// </summary>
    [Table("CameraConfig")]
    public class CameraConfig
    {
        /// <summary>Camera display name (e.g. "HD User Facing", "FLIR Video").</summary>
        [PrimaryKey]   // SQLite-net composite PK requires both columns to be [PrimaryKey]
        [MaxLength(256)]
        public string DeviceName { get; set; } = string.Empty;

        /// <summary>
        /// GStreamer provider type: "MF" (Media Foundation) or "KS" (Kernel Streaming).
        /// Caps negotiated on one provider are not valid on the other.
        /// </summary>
        [PrimaryKey]
        [MaxLength(8)]
        public string ProviderType { get; set; } = string.Empty;

        /// <summary>
        /// GStreamer caps string confirmed as working, e.g.
        /// "video/x-raw,format=NV12,width=1280,height=720,framerate=30/1"
        /// </summary>
        [MaxLength(512)]
        public string Caps { get; set; } = string.Empty;

        /// <summary>
        /// True when a gray-conversion stage must be inserted in the pipeline
        /// (thermal cameras outputting GRAY16_LE need this).
        /// </summary>
        public bool NeedsGrayConvert { get; set; }

        /// <summary>UTC timestamp of the last successful stream with these caps.</summary>
        public DateTime LastSuccessUtc { get; set; }

        /// <summary>How many times these caps have been confirmed working.</summary>
        public int SuccessCount { get; set; }

        /// <summary>
        /// Consecutive failures since the last success.
        /// When this reaches the threshold the record is treated as stale and
        /// the camera is re-probed on the next startup.
        /// </summary>
        public int ConsecutiveFailures { get; set; }

        /// <summary>
        /// When true, the camera has been permanently marked as unable to stream
        /// on this provider (e.g. FLIR on MF with no KS driver).  The worker
        /// will not be started for this (DeviceName, ProviderType) combination.
        /// Reset by deleting the record or calling DeleteCameraConfigAsync.
        /// </summary>
        public bool PermanentlyUnsupported { get; set; }
    }
}