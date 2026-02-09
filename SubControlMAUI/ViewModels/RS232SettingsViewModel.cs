using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SubControlMAUI.Services;
using System;
using System.Collections.Generic;
using System.Text;

namespace SubControlMAUI.ViewModels
{
    public partial class RS232SettingsViewModel : BaseViewModel
    {
        IAlertService _alertService;
        SQLiteService _sqlLiteService;
        public RS232SettingsViewModel(IAlertService alertService, SQLiteService sqlLiteService)
        {
            Title = "RS232 Settings";
            _alertService = alertService;
            _sqlLiteService = sqlLiteService;
            LoadSettings();
        }


        [ObservableProperty]
        string selectedCommPort;

        [ObservableProperty]
        List<string> commPortList;

        [ObservableProperty]
        List<string> baudRateList;

        [ObservableProperty]
        string selectedBaudRate;

        [ObservableProperty]
        List<string> parityList;

        [ObservableProperty]
        string selectedParity;

        [ObservableProperty]
        List<string> dataBitsList;

        [ObservableProperty]
        string selectedDataBits;

        [ObservableProperty]
        List<string> stopBitsList;

        [ObservableProperty]
        string selectedStopBits;

        [ObservableProperty]
        List<string> flowControlList;

        [ObservableProperty]
        string selectedFlowControl;

        [RelayCommand]
        async Task Save()
        {
            if (await ValidateSettings())
            {
                _sqlLiteService.config.Rs232Port = SelectedCommPort;
                _sqlLiteService.config.BaudRate = Int32.Parse(SelectedBaudRate);
                _sqlLiteService.config.Parity = SelectedParity;
                _sqlLiteService.config.DataBits = Int32.Parse(SelectedDataBits);
                _sqlLiteService.config.StopBits = SelectedStopBits;
                _sqlLiteService.config.FlowControl = SelectedFlowControl;
                //_sqlLiteService.config.PeriscopeUpCommand = UpCommand;
                //_sqlLiteService.config.PeriscopeDownCommand = DownCommand;
                //_sqlLiteService.config.CutterLeftCommand = LeftCommand;
                //_sqlLiteService.config.CutterRightCommand = RightCommand;
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
            return true;
        }

        private void LoadSettings()
        {

            SelectedCommPort = _sqlLiteService.config.Rs232Port;
            CommPortList = new List<string> { "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9", "COM10" };
            SelectedBaudRate = _sqlLiteService.config.BaudRate.ToString();
            BaudRateList = new List<string> { "1200", "2400", "4800", "9600", "19200", "38400", "57600", "115200" };
            SelectedParity = _sqlLiteService.config.Parity;
            ParityList = new List<string> { "None", "Even", "Odd", "Mark", "Space" };
            SelectedDataBits = _sqlLiteService.config.DataBits.ToString();
            DataBitsList = new List<string> { "5", "6", "7", "8" };
            SelectedStopBits = _sqlLiteService.config.StopBits;
            StopBitsList = new List<string> { "None", "One", "OnePointFive", "Two" };
            SelectedFlowControl = _sqlLiteService.config.FlowControl;
            FlowControlList = new List<string> { "None", "XOnXOff", "Hardware" };

        }

    }
}
