using Microsoft.AspNetCore.DataProtection.KeyManagement;
using Microsoft.Extensions.Logging;
using SQLite;
using SubConsole.Models;
using SubConsole.Services.Video;
using SubControl.Model;
using System.Net.NetworkInformation;
using System.Reflection;
using System.Text.Json;
using static SubConsole.Models.UsbDeviceInfo;
using static System.Net.WebRequestMethods;

namespace SubConsole.Services.SQL
{
    public class SQLiteService
    {
        // https://github.com/praeclarum/sqlite-net
        SQLiteAsyncConnection Database;

        public bool DefaultsLoaded { get; set; } = false;
        public bool ConfigLoadedError { get; set; } = false;

        private readonly ILogger<SQLiteService> _logger;

        public SQLiteService(ILogger<SQLiteService> logger)
        {
            _logger = logger;
        }

        //    public Config config { get; set; }

        private async Task<OperationResult> Init()
        {
            try
            {
                if (Database is not null)
                {
                    _logger.LogDebug("SQLite already initialized");
                    return OperationResult.Success();
                }

                string basePath = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                string appFolder = Path.Combine(basePath, "SubControl");
                Directory.CreateDirectory(appFolder);

                string dbPath = Path.Combine(appFolder, "mydb.sqlite");

                _logger.LogInformation(
                    "Initializing SQLite database at {DatabasePath}",
                    dbPath);

                Database = new SQLiteAsyncConnection(dbPath, Constants.Flags);

                await Database.CreateTableAsync<DeviceRegistrationEntity>();
                await Database.CreateTableAsync<CameraRegistrationEntity>();  // ← new

                _logger.LogInformation("SQLite initialized successfully");
                return OperationResult.Success();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to initialize SQLite database");
                return OperationResult.Failure($"Exception: {ex.Message}");
            }
        }

        public async Task<OperationResult> DropAllTablesAsync()
        {
            try
            {
                await Init();
                _logger.LogWarning("Dropping all SQLite tables");

                await Database.DropTableAsync<DeviceRegistrationEntity>();
                await Database.DropTableAsync<CameraRegistrationEntity>();  // ← new

                _logger.LogInformation("All tables dropped successfully");
                return OperationResult.Success();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to drop tables");
                return OperationResult.Failure($"Exception: {ex.Message}");
            }
        }

        #region CameraRegistry

        public async Task<OperationResultWithValue<List<CameraRegistration>>> GetCameraRegistrationsAsync()
        {
            try
            {
                await Init();

                _logger.LogDebug("Fetching camera registrations from database");

                var entities = await Database.Table<CameraRegistrationEntity>().ToListAsync();

                var result = entities
                    .Select(CameraRegistrationEntity.ToModel)
                    .ToList();

                _logger.LogInformation(
                    "Loaded {Count} camera registrations from database",
                    result.Count);

                return OperationResultWithValue<List<CameraRegistration>>.Success(result);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to load camera registrations");
                return OperationResultWithValue<List<CameraRegistration>>.Failure(
                    $"Exception: {ex.Message}");
            }
        }

        public async Task<OperationResult> UpsertCameraRegistrationAsync(CameraRegistration model)
        {
            try
            {
                await Init();

                _logger.LogInformation(
                    "Upserting camera registration {DeviceId} (StreamPath={StreamPath}, MtxRegistered={IsRegistered})",
                    model.Camera.DeviceId,
                    model.StreamPathName,
                    model.IsRegisteredWithMtx);

                var entity = CameraRegistrationEntity.ToEntity(model);
                var rows = await Database.InsertOrReplaceAsync(entity);

                if (rows != 1)
                {
                    _logger.LogWarning(
                        "Failed to upsert camera registration {DeviceId}: returned {Rows} row(s), expected 1",
                        model.Camera.DeviceId,
                        rows);
                    return OperationResult.Failure(
                        $"Failed to upsert camera registration {model.Camera.DeviceId}: " +
                        $"returned {rows} row(s), expected 1");
                }

                _logger.LogDebug(
                    "Upsert result for {DeviceId}: {RowsAffected} row(s)",
                    model.Camera.DeviceId,
                    rows);

                return OperationResult.Success();
            }
            catch (Exception ex)
            {
                _logger.LogError(
                    ex,
                    "Failed to upsert camera registration {DeviceId}",
                    model.Camera.DeviceId);
                return OperationResult.Failure($"Exception: {ex.Message}");
            }
        }

        public async Task<OperationResult> DeleteCameraRegistrationAsync(string deviceId)
        {
            try
            {
                await Init();

                _logger.LogInformation(
                    "Deleting camera registration {DeviceId}", deviceId);

                var rows = await Database.Table<CameraRegistrationEntity>()
                    .Where(x => x.DeviceId == deviceId)
                    .DeleteAsync();

                if (rows != 1)
                {
                    _logger.LogWarning(
                        "Failed to delete camera registration {DeviceId}: returned {Rows} row(s), expected 1",
                        deviceId,
                        rows);
                    return OperationResult.Failure(
                        $"Failed to delete camera registration {deviceId}: " +
                        $"returned {rows} row(s), expected 1");
                }

                _logger.LogDebug(
                    "Delete result for {DeviceId}: {RowsAffected} row(s)",
                    deviceId,
                    rows);

                return OperationResult.Success();
            }
            catch (Exception ex)
            {
                _logger.LogError(
                    ex,
                    "Failed to delete camera registration {DeviceId}",
                    deviceId);
                return OperationResult.Failure($"Exception: {ex.Message}");
            }
        }

        public async Task<OperationResult> DeleteAllCameraRegistrationsAsync()
        {
            try
            {
                await Init();

                _logger.LogWarning("Deleting ALL camera registrations");

                var rows = await Database.DeleteAllAsync<CameraRegistrationEntity>();

                _logger.LogInformation(
                    "Deleted {RowsAffected} camera registrations", rows);

                return OperationResult.Success();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to delete all camera registrations");
                return OperationResult.Failure($"Exception: {ex.Message}");
            }
        }

        #endregion


        #region DeviceRegistry

        public async Task<OperationResultWithValue<List<DeviceRegistration>>> GetDeviceRegistriesAsync()
        {
            try
            {
                await Init();

                _logger.LogDebug("Fetching device registrations from database");

                var entities = await Database.Table<DeviceRegistrationEntity>().ToListAsync();

                var result = new List<DeviceRegistration>();

                foreach (var entity in entities)
                {
                    var identifier = JsonSerializer.Deserialize<UsbSerialPortInfo>(entity.IdentifierJson)
                        ?? throw new Exception("Failed to deserialize Identifier");

                    SerialPortSettings? portSettings = string.IsNullOrWhiteSpace(entity.PortSettingsJson)
                        ? null
                        : JsonSerializer.Deserialize<SerialPortSettings>(entity.PortSettingsJson);

                    var model = new DeviceRegistration(
                        identifier,
                        entity.FunctionName,
                        entity.BaudRate,
                        (SerialWorkerType)entity.SerialWorkerType
                    )
                    {
                        CurrentPortPath = entity.CurrentPortPath,
                        PortSettings = portSettings
                    };

                    result.Add(model);
                }
                _logger.LogInformation(
                    "Loaded {Count} device registrations from database",
                    result.Count);
                return OperationResultWithValue<List<DeviceRegistration>>.Success(result);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to load device registrations");
                return OperationResultWithValue<List<DeviceRegistration>>.Failure($"Exception: {ex.Message}");

            }
        }


        public async Task<OperationResult> UpsertDeviceRegistrationAsync(DeviceRegistration model)
        {
            try
            {
                await Init();

                _logger.LogInformation(
                    "Upserting device registration {DeviceKey} (Function={FunctionName}, Port={PortPath})",
                    model.Key,
                    model.FunctionName,
                    model.CurrentPortPath);

                var entity = new DeviceRegistrationEntity
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

                var rows = await Database.InsertOrReplaceAsync(entity);

                if(rows != 1)
                {
                    _logger.LogWarning(
                        "Failed to upsert device registration {DeviceKey} update returned:{Rows} expecting:1",
                        model.Key,
                        rows);
                    return OperationResult.Failure($"Failed to upsert device registration {model.Key} update returned:{rows} expecting:1");
                }

                _logger.LogDebug(
                    "Upsert result for {DeviceKey}: {RowsAffected} row(s)",
                    model.Key,
                    rows);

                return OperationResult.Success();
            }
            catch (Exception ex)
            {
                _logger.LogError(
                    ex,
                    "Failed to upsert device registration {DeviceKey}",
                    model.Key);
                return OperationResult.Failure($"Exception: {ex.Message}");
            }
        }


        public async Task<OperationResult> DeleteDeviceRegistrationAsync(string key)
        {
            try
            {
                await Init();

                _logger.LogInformation(
                    "Deleting device registration {DeviceKey}",
                    key);

                var rows = await Database.Table<DeviceRegistrationEntity>()
                    .Where(x => x.Key == key)
                    .DeleteAsync();

                if (rows != 1)
                {
                    _logger.LogWarning(
                        "Failed to delete device registration {DeviceKey} update returned:{Rows} expecting:1",
                        key,
                        rows);
                    return OperationResult.Failure($"Failed to delete device registration {key} update returned:{rows} expecting:1");
                }

                _logger.LogDebug(
                    "Delete result for {DeviceKey}: {RowsAffected} row(s)",
                    key,
                    rows);

                return OperationResult.Success();
            }
            catch (Exception ex)
            {
                _logger.LogError(
                    ex,
                    "Failed to delete device registration {DeviceKey}",
                    key);
                return OperationResult.Failure($"Exception: {ex.Message}");
            }
        }

        public async Task<OperationResult> DeleteAllDeviceRegistrationsAsync()
        {
            try
            {
                await Init();

                _logger.LogWarning("Deleting ALL device registrations");

                var rows = await Database.DeleteAllAsync<DeviceRegistrationEntity>();

                _logger.LogInformation(
                    "Deleted {RowsAffected} device registrations",
                    rows);

                return OperationResult.Success();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to delete all device registrations");
                return OperationResult.Failure($"Exception: {ex.Message}");
            }
        }



        #endregion

        #region "Config"

        //public async Task<bool> GetConfigAsync()
        //{
        //    try
        //    {
        //        await Init();
        //        config = await Database.Table<Config>().Where(i => i.Id == 0).FirstOrDefaultAsync();
        //        return config is not null;
        //    }
        //    catch (Exception ex)
        //    {
        //        LastError = ex.Message;
        //        return false;
        //    }
        //}

        //public async Task<bool> SetDefaultConfig()
        //{
        //    config = new Config
        //    {
        //        Id = 0,
        //        IPAddress = "127.0.0.1",
        //        Port = "9000",
        //        Rs232Port = "COM1",
        //        BaudRate = 9600,
        //        Parity = "None",
        //        DataBits = 8,
        //        StopBits = "One",
        //        FlowControl = "None",
        //        BusId = 1,
        //        DeviceAddress = 60,
        //        ClockRate = 1000000,
        //        CutterUpCommand = "CUTTER_UP",
        //        CutterDownCommand = "CUTTER_DOWN",
        //        CutterLeftCommand = "CUTTER_LEFT",
        //        CutterRightCommand = "CUTTER_RIGHT",
        //        PeriscopeUpCommand = "PERISCOPE_UP",
        //        PeriscopeDownCommand = "PERISCOPE_DOWN",
        //        Features = "CUTTER,PERISCOPE"
        //    };
        //    return await SaveConfigAsync(false) == 1;
        //}

        //public async Task<int> SaveConfigAsync(bool isUpdate)
        //{
        //    try
        //    {
        //        await Init();
        //        return isUpdate
        //            ? await Database.UpdateAsync(config)
        //            : await Database.InsertAsync(config);
        //    }
        //    catch (Exception ex)
        //    {
        //        LastError = ex.Message;
        //        return -1;
        //    }
        //}

        //public async Task<int> DeleteAllConfig()
        //{
        //    try
        //    {
        //        await Init();
        //        return await Database.DeleteAllAsync<Config>();
        //    }
        //    catch (Exception ex)
        //    {
        //        LastError = ex.Message;
        //        return -1;
        //    }
        //}




        //public async Task<bool> SetDefaultConfig()
        //{
        //    config = new Config
        //    {
        //        Id = 0,
        //        IPAddress = "127.0.0.1",
        //        Port = "9000",
        //        Rs232Port = "COM1",
        //        BaudRate = 9600,
        //        Parity = "None",
        //        DataBits = 8,
        //        StopBits = "One",
        //        FlowControl = "None",
        //        BusId = 1,
        //        DeviceAddress = 60,
        //        ClockRate = 1000000,
        //        CutterUpCommand = "CUTTER_UP",
        //        CutterDownCommand = "CUTTER_DOWN",
        //        CutterLeftCommand = "CUTTER_LEFT",
        //        CutterRightCommand = "CUTTER_RIGHT",
        //        PeriscopeUpCommand = "PERISCOPE_UP",
        //        PeriscopeDownCommand = "PERISCOPE_DOWN",
        //        Features = "CUTTER,PERISCOPE"
        //    };
        //    return await SaveConfigAsync(false) == 1;
        //}

        //public async Task<int> SaveConfigAsync(bool isUpdate)
        //{
        //    try
        //    {
        //        await Init();
        //        return isUpdate
        //            ? await Database.UpdateAsync(config)
        //            : await Database.InsertAsync(config);
        //    }
        //    catch (Exception ex)
        //    {
        //        LastError = ex.Message;
        //        return -1;
        //    }
        //}

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