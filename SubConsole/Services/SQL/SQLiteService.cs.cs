using SQLite;
using SubConsole.Models;
using SubControl.Model;
using System.Net.NetworkInformation;
using System.Text.Json;
using static SubConsole.Models.UsbDeviceInfo;

namespace SubConsole.Services.SQL
{
    public class SQLiteService
    {
        // https://github.com/praeclarum/sqlite-net
        SQLiteAsyncConnection Database;

        public bool DefaultsLoaded { get; set; } = false;
        public bool ConfigLoadedError { get; set; } = false;

        public SQLiteService() { }

    //    public Config config { get; set; }

        private async Task<OperationResult> Init()
        {
            try
            {
                if (Database is not null)
                    return OperationResult.Success();

                string basePath = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                string appFolder = Path.Combine(basePath, "SubControl");
                Directory.CreateDirectory(appFolder);

                string dbPath = Path.Combine(appFolder, "mydb.sqlite");

                Database = new SQLiteAsyncConnection(dbPath, Constants.Flags);
                //        await Database.CreateTableAsync<Config>();
                await Database.CreateTableAsync<DeviceRegistrationEntity>();
                return OperationResult.Success();

            }
            catch (Exception ex)
            {
                return OperationResult.Failure($"Exception: {ex.Message}");
            }
        }

        public async Task<OperationResult> DropAllTablesAsync()
        {
            try
            {
                await Init();
                await Database.DropTableAsync<DeviceRegistrationEntity>();
                //  await Database.DropTableAsync<Config>();
                //   await Database.DropTableAsync<CameraConfig>();
                return OperationResult.Success();
            }
            catch (Exception ex)
            {
                return OperationResult.Failure($"Exception: {ex.Message}");
            }
        }

     


        #region DeviceRegistry

        public async Task<OperationResultWithValue<List<DeviceRegistration>>> GetDeviceRegistriesAsync()
        {
            try
            {
                await Init();

                var entities = await Database.Table<DeviceRegistrationEntity>().ToListAsync();

                var result = new List<DeviceRegistration>();

                foreach (var entity in entities)
                {
                    var identifier = JsonSerializer.Deserialize<UsbSerialPortInfo>(entity.IdentifierJson)
                 ?? throw new Exception("Failed to deserialize Identifier");

                    var model = new DeviceRegistration(
                        identifier,
                        entity.FunctionName,
                        entity.BaudRate,
                        (SerialWorkerType)entity.SerialWorkerType
                    )
                    {
                        CurrentPortPath = entity.CurrentPortPath
                    };

                    result.Add(model);
                }

                return OperationResultWithValue<List<DeviceRegistration>>.Success(result);
            }
            catch (Exception ex)
            {
                return OperationResultWithValue<List<DeviceRegistration>>.Failure($"Exception: {ex.Message}");

            }
        }


        public async Task<OperationResult> UpsertDeviceRegistrationAsync(DeviceRegistration model)
        {
            try
            {
                await Init();

                var entity = new DeviceRegistrationEntity
                {
                    Key = model.Key,
                    FunctionName = model.FunctionName,
                    BaudRate = model.BaudRate,
                    SerialWorkerType = (int)model.SerialWorkerType,
                    IdentifierJson = JsonSerializer.Serialize(model.Identifier),
                    CurrentPortPath = model.CurrentPortPath
                };

                await Database.InsertOrReplaceAsync(entity);

                return OperationResult.Success();
            }
            catch (Exception ex)
            {
                return OperationResult.Failure($"Exception: {ex.Message}");
            }
        }


        public async Task<OperationResult> DeleteDeviceRegistrationAsync(string key)
        {
            try
            {
                await Init();

                var rows = await Database.Table<DeviceRegistrationEntity>()
                    .Where(x => x.Key == key)
                    .DeleteAsync();

                return OperationResult.Success();
            }
            catch (Exception ex)
            {
                return OperationResult.Failure($"Exception: {ex.Message}");
            }
        }

        public async Task<OperationResult> DeleteAllDeviceRegistrationsAsync()
        {
            try
            {
                await Init();

                await Database.DeleteAllAsync<DeviceRegistrationEntity>();

                return OperationResult.Success();
            }
            catch (Exception ex)
            {
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