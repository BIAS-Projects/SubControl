
using SQLite;
using SubControlMAUI.Model;
using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations.Schema;
using System.Data;
using System.Linq;
using System.Reflection.Metadata;
using System.Text;
using System.Threading.Tasks;

namespace SubControlMAUI.Services
{
    public class SQLiteService
    {

        //https://github.com/praeclarum/sqlite-net
        SQLiteAsyncConnection Database;

        public string LastError { get; set; }

        public bool DefaultsLoaded { get; set; } = false;

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

                Database = new SQLiteAsyncConnection(Constants.DatabasePath, Constants.Flags);
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
                UpCommand = "UP",
                DownCommand = "DOWN",
                LeftCommand = "LEFT",
                RightCommand = "RIGHT"
            };
            if(await SaveConfigAsync(config, false) == 1)
            { 
                return true;
            }
            else
            {
                return false;
            }
        }
        public async Task<int> SaveConfigAsync(Config config, bool isUpdate)
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

        public static string DatabasePath =>
        //Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), DatabaseFilename);
        Path.Combine(FileSystem.Current.CacheDirectory, DatabaseFilename);
    }
}
