
using SQLite;
using SubControl.Model;

namespace SubControlMAUI.Services
{
    public class SQLiteService
    {

        //https://github.com/praeclarum/sqlite-net
        SQLiteAsyncConnection Database;

        public string LastError { get; set; } = string.Empty;

        public bool DefaultsLoaded { get; set; } = false;

        public bool ConfigLoadedError { get; set; } = false;

        public SQLiteService()
        {
        }

        public Config config { get; set; }

        async Task Init()
        {
            try
            {
                if (Database is not null)
                    return;

                //C:\Users\gavin\AppData\Local\User Name\com.companyname.subcontrolmaui\Cache


                string basePath = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                string appFolder = Path.Combine(basePath, "SubControl");
                Directory.CreateDirectory(appFolder);

                string dbPath = Path.Combine(appFolder, "mydb.sqlite");

                Database = new SQLiteAsyncConnection(dbPath, Constants.Flags);
                //CreateTableAsync returns an enum reporting how the table was created, which is not important for this solution
                await Database.CreateTableAsync<Config>();



            }
            catch (Exception ex)
            {
                LastError = ex.Message;
                return;
            }
        }


        public async Task<bool> DropAllTablesAsync()
        {
            try
            {
                await Init();
                await Database.DropTableAsync<Config>();
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
                if(config is null)
                {
                    return false;
                }
                else
                {
                    return true;
                }
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
            if(await SaveConfigAsync(false) == 1)
            { 
                return true;
            }
            else
            {
                return false;
            }
        }
        public async Task<int> SaveConfigAsync(bool isUpdate)
        {
            try
            {
                await Init();

                if (isUpdate)
                    return await Database.UpdateAsync(config);
                else
                    return await Database.InsertAsync(config);
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

  
    }

    public static class Constants
    {
        public const string DatabaseFilename = "Settings.db3";

        public const SQLite.SQLiteOpenFlags Flags =
            // open the database in read/write mode
            SQLite.SQLiteOpenFlags.ReadWrite |
            // create the database if it doesn't exist
            SQLite.SQLiteOpenFlags.Create |
            // enable multi-threaded database access
            SQLite.SQLiteOpenFlags.SharedCache;

        //public static string DatabasePath =>
        //Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), DatabaseFilename);
        //Path.Combine(FileSystem.Current.CacheDirectory, DatabaseFilename);

    }
}
