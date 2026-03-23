using SQLite;
using SubControl.Model;

namespace SubControlMAUI.Services
{
    public class SQLiteService
    {
        // https://github.com/praeclarum/sqlite-net
        SQLiteAsyncConnection Database;

        public string LastError { get; set; } = string.Empty;
        public bool DefaultsLoaded { get; set; } = false;
        public bool ConfigLoadedError { get; set; } = false;

        public SQLiteService() { }

        public Config config { get; set; }

        async Task Init()
        {
            try
            {
                if (Database is not null)
                    return;

                string basePath = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                string appFolder = Path.Combine(basePath, "SubControl");
                Directory.CreateDirectory(appFolder);

                string dbPath = Path.Combine(appFolder, "mydb.sqlite");

                Database = new SQLiteAsyncConnection(dbPath, Constants.Flags);
                await Database.CreateTableAsync<Config>();
                await Database.CreateTableAsync<CameraConfig>();
            }
            catch (Exception ex)
            {
                LastError = ex.Message;
            }
        }

        public async Task<bool> DropAllTablesAsync()
        {
            try
            {
                await Init();
                await Database.DropTableAsync<Config>();
                await Database.DropTableAsync<CameraConfig>();
                return true;
            }
            catch (Exception ex)
            {
                LastError = ex.Message;
                return false;
            }
        }

        #region "Config"

        public async Task<bool> GetConfigAsync()
        {
            try
            {
                await Init();
                config = await Database.Table<Config>().Where(i => i.Id == 0).FirstOrDefaultAsync();
                return config is not null;
            }
            catch (Exception ex)
            {
                LastError = ex.Message;
                return false;
            }
        }

        public async Task<bool> SetDefaultConfig()
        {
            config = new Config
            {
                Id = 0,
                IPAddress = "127.0.0.1",
                Port = "9000",
                Rs232Port = "COM1",
                BaudRate = 9600,
                Parity = "None",
                DataBits = 8,
                StopBits = "One",
                FlowControl = "None",
                BusId = 1,
                DeviceAddress = 60,
                ClockRate = 1000000,
                CutterUpCommand = "CUTTER_UP",
                CutterDownCommand = "CUTTER_DOWN",
                CutterLeftCommand = "CUTTER_LEFT",
                CutterRightCommand = "CUTTER_RIGHT",
                PeriscopeUpCommand = "PERISCOPE_UP",
                PeriscopeDownCommand = "PERISCOPE_DOWN",
                Features = "CUTTER,PERISCOPE"
            };
            return await SaveConfigAsync(false) == 1;
        }

        public async Task<int> SaveConfigAsync(bool isUpdate)
        {
            try
            {
                await Init();
                return isUpdate
                    ? await Database.UpdateAsync(config)
                    : await Database.InsertAsync(config);
            }
            catch (Exception ex)
            {
                LastError = ex.Message;
                return -1;
            }
        }

        public async Task<int> DeleteAllConfig()
        {
            try
            {
                await Init();
                return await Database.DeleteAllAsync<Config>();
            }
            catch (Exception ex)
            {
                LastError = ex.Message;
                return -1;
            }
        }

        #endregion

        #region "CameraConfig"

        // ------------------------------------------------------------------ //
        //  Provider-type helpers                                               //
        // ------------------------------------------------------------------ //

        /// <summary>
        /// Derives the provider type string from a GStreamer device path string:
        ///   /GstMFDeviceProvider:.../GstMFDevice:mfdevice0     →  "MF"   (Windows Media Foundation)
        ///   /GstKsDeviceProvider:.../GstKsDevice:ksdevice0     →  "KS"   (Windows Kernel Streaming)
        ///   /GstV4l2DeviceProvider:.../GstV4l2Device:...       →  "V4L2" (Linux Video4Linux2)
        /// Falls back to "Unknown" if the path matches no known pattern.
        /// </summary>
        public static string ProviderTypeFromDevicePath(string devicePath)
        {
            if (devicePath.Contains("mfdevice", StringComparison.OrdinalIgnoreCase)) return "MF";
            if (devicePath.Contains("ksdevice", StringComparison.OrdinalIgnoreCase)) return "KS";
            if (devicePath.Contains("v4l2", StringComparison.OrdinalIgnoreCase)) return "V4L2";
            return "Unknown";
        }

        // ------------------------------------------------------------------ //
        //  Read                                                                //
        // ------------------------------------------------------------------ //

        /// <summary>
        /// Returns the persisted caps for a camera on a specific provider, or null
        /// if the camera has never been seen on that provider, its saved caps have
        /// too many consecutive failures, or it has been permanently marked as
        /// unsupported on that provider.
        /// </summary>
        public async Task<CameraConfig?> GetCameraConfigAsync(string deviceName, string providerType)
        {
            try
            {
                await Init();
                var row = await Database.Table<CameraConfig>()
                    .Where(c => c.DeviceName == deviceName && c.ProviderType == providerType)
                    .FirstOrDefaultAsync();

                if (row is null)
                    return null;

                // Permanently unsupported — tell the caller to skip this device entirely
                if (row.PermanentlyUnsupported)
                    return null;

                // Stale caps — too many failures since the last success, force re-probe
                if (row.ConsecutiveFailures >= 3)
                    return null;

                return row;
            }
            catch (Exception ex)
            {
                LastError = ex.Message;
                return null;
            }
        }

        /// <summary>
        /// Returns true if a camera has been permanently marked as unsupported on
        /// the given provider (e.g. a FLIR camera on MF with no KS driver).
        /// </summary>
        public async Task<bool> IsPermanentlyUnsupportedAsync(string deviceName, string providerType)
        {
            try
            {
                await Init();
                var row = await Database.Table<CameraConfig>()
                    .Where(c => c.DeviceName == deviceName && c.ProviderType == providerType)
                    .FirstOrDefaultAsync();
                return row?.PermanentlyUnsupported ?? false;
            }
            catch (Exception ex)
            {
                LastError = ex.Message;
                return false;
            }
        }

        // ------------------------------------------------------------------ //
        //  Write — success                                                     //
        // ------------------------------------------------------------------ //

        /// <summary>
        /// Called when a camera starts streaming successfully.  Either inserts a
        /// new record or updates the existing one, resetting the failure counter
        /// and clearing any permanently-unsupported flag.
        /// </summary>
        public async Task RecordCameraSuccessAsync(
            string deviceName, string providerType, string caps, bool needsGrayConvert)
        {
            try
            {
                await Init();
                var existing = await Database.Table<CameraConfig>()
                    .Where(c => c.DeviceName == deviceName && c.ProviderType == providerType)
                    .FirstOrDefaultAsync();

                if (existing is null)
                {
                    await Database.InsertAsync(new CameraConfig
                    {
                        DeviceName = deviceName,
                        ProviderType = providerType,
                        Caps = caps,
                        NeedsGrayConvert = needsGrayConvert,
                        LastSuccessUtc = DateTime.UtcNow,
                        SuccessCount = 1,
                        ConsecutiveFailures = 0,
                        PermanentlyUnsupported = false,
                    });
                }
                else
                {
                    existing.Caps = caps;
                    existing.NeedsGrayConvert = needsGrayConvert;
                    existing.LastSuccessUtc = DateTime.UtcNow;
                    existing.SuccessCount += 1;
                    existing.ConsecutiveFailures = 0;
                    existing.PermanentlyUnsupported = false;
                    await Database.UpdateAsync(existing);
                }
            }
            catch (Exception ex)
            {
                LastError = ex.Message;
            }
        }

        // ------------------------------------------------------------------ //
        //  Write — failure                                                     //
        // ------------------------------------------------------------------ //

        /// <summary>
        /// Called when the pipeline fails after previously working caps.
        /// Increments the failure counter; when it reaches the threshold the
        /// record is treated as stale and the camera re-probes on next startup.
        /// </summary>
        public async Task RecordCameraFailureAsync(string deviceName, string providerType)
        {
            try
            {
                await Init();
                var existing = await Database.Table<CameraConfig>()
                    .Where(c => c.DeviceName == deviceName && c.ProviderType == providerType)
                    .FirstOrDefaultAsync();

                if (existing is null) return;

                existing.ConsecutiveFailures += 1;
                await Database.UpdateAsync(existing);
            }
            catch (Exception ex)
            {
                LastError = ex.Message;
            }
        }

        /// <summary>
        /// Permanently marks a (device, provider) combination as unable to stream.
        /// The worker will not be started for this combination on future runs.
        /// Used when a camera exhausts all retry attempts with a consistent
        /// not-negotiated error (e.g. FLIR on MF with no compatible driver).
        /// </summary>
        public async Task MarkPermanentlyUnsupportedAsync(string deviceName, string providerType)
        {
            try
            {
                await Init();
                var existing = await Database.Table<CameraConfig>()
                    .Where(c => c.DeviceName == deviceName && c.ProviderType == providerType)
                    .FirstOrDefaultAsync();

                if (existing is null)
                {
                    await Database.InsertAsync(new CameraConfig
                    {
                        DeviceName = deviceName,
                        ProviderType = providerType,
                        Caps = string.Empty,
                        PermanentlyUnsupported = true,
                    });
                }
                else
                {
                    existing.PermanentlyUnsupported = true;
                    await Database.UpdateAsync(existing);
                }
            }
            catch (Exception ex)
            {
                LastError = ex.Message;
            }
        }

        // ------------------------------------------------------------------ //
        //  Utility                                                             //
        // ------------------------------------------------------------------ //

        /// <summary>Returns all persisted camera configs, for diagnostics / UI.</summary>
        public async Task<List<CameraConfig>> GetAllCameraConfigsAsync()
        {
            try
            {
                await Init();
                return await Database.Table<CameraConfig>().ToListAsync();
            }
            catch (Exception ex)
            {
                LastError = ex.Message;
                return new List<CameraConfig>();
            }
        }

        /// <summary>
        /// Deletes the persisted config for a specific (device, provider) pair,
        /// forcing a full re-probe next time it is detected.  Also clears any
        /// permanently-unsupported flag.
        /// </summary>
        public async Task<bool> DeleteCameraConfigAsync(string deviceName, string providerType)
        {
            try
            {
                await Init();
                var existing = await Database.Table<CameraConfig>()
                    .Where(c => c.DeviceName == deviceName && c.ProviderType == providerType)
                    .FirstOrDefaultAsync();
                if (existing is not null)
                    await Database.DeleteAsync(existing);
                return true;
            }
            catch (Exception ex)
            {
                LastError = ex.Message;
                return false;
            }
        }

        #endregion
    }

    public static class Constants
    {
        public const string DatabaseFilename = "Settings.db3";

        public const SQLite.SQLiteOpenFlags Flags =
            SQLite.SQLiteOpenFlags.ReadWrite |
            SQLite.SQLiteOpenFlags.Create |
            SQLite.SQLiteOpenFlags.SharedCache;
    }
}