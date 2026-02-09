using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SubControlMAUI.Services;
using System;
using System.Collections.Generic;
using System.Text;


namespace SubControlMAUI.ViewModels
{
    public partial class I2CSettingsViewModel : BaseViewModel
    {
        IAlertService _alertService;
        SQLiteService _sqlLiteService;

        public I2CSettingsViewModel(IAlertService alertService, SQLiteService sqlLiteService)
        {
            Title = "I2C Settings";
            _alertService = alertService;
            _sqlLiteService = sqlLiteService;
            LoadSettings();

        }

        [ObservableProperty]
        string selectedBusID;

        [ObservableProperty]
        List<string> busIDList;

        [ObservableProperty]
        List<string> deviceAddressList;

        [ObservableProperty]
        string selectedDeviceAddress;

        //[ObservableProperty]
        //List<string> clockRateList;

        //[ObservableProperty]
        //string selectedClockRate;

        


        [RelayCommand]
        async Task Save()
        {
            if (await ValidateSettings())
            {
                _sqlLiteService.config.BusId = Int32.Parse(SelectedBusID);
                _sqlLiteService.config.DeviceAddress = Int32.Parse(SelectedDeviceAddress);
                //_sqlLiteService.config.ClockRate = Int32.Parse(SelectedClockRate);
                if (await _sqlLiteService.SaveConfigAsync(true) == 1)
                {
                    await _alertService.ShowAlertAsync("Informarion", $"Settings Saved Successfully", "OK");
                }
                else
                {
                    await _alertService.ShowAlertAsync("Error", $"Error Saving Settings: {_sqlLiteService.LastError}", "OK");
                }
            }
        }


        private async Task<bool> ValidateSettings()
        {
            //if (string.IsNullOrWhiteSpace(UpCommand))
            //{
            //    await _alertService.ShowAlertAsync("Error", $"Up Command Is Empty", "OK");
            //    return false;
            //}
            //if (string.IsNullOrWhiteSpace(DownCommand))
            //{
            //    await _alertService.ShowAlertAsync("Error", $"Down Command Is Empty", "OK");
            //    return false;
            //}
            //if (string.IsNullOrWhiteSpace(LeftCommand))
            //{
            //    await _alertService.ShowAlertAsync("Error", $"Left Command Is Empty", "OK");
            //    return false;
            //}
            //if (string.IsNullOrWhiteSpace(RightCommand))
            //{
            //    await _alertService.ShowAlertAsync("Error", $"Right Command Is Empty", "OK");
            //    return false;
            //}
            return true;

        }

        private void LoadSettings()
        {


            //Address0 to 127(aka 0 to 0x7F hex)
            // However, two blocks of addresses at the beginning(0x00 - 0x07) and end(0x78 - 0x7F) of the range are reserved for special functions.
            BusIDList = new List<string>();
            for (int i = 1; i < 11; i++)
            {
                BusIDList.Add(i.ToString());
            }
            DeviceAddressList = new List<string>();
            for (int i = 8; i < 113; i++)
            {
                DeviceAddressList.Add(i.ToString());
            }
            SelectedDeviceAddress = _sqlLiteService.config.DeviceAddress.ToString();
            SelectedBusID = _sqlLiteService.config.BusId.ToString();

    //        SelectedClockRate = _sqlLiteService.config.ClockRate.ToString();
        }








    }
}
