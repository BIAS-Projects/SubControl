using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SubControlMAUI.Messages;
using SubControlMAUI.Services;
using System;
using System.Collections.Generic;
using System.Net;
using System.Text;

namespace SubControlMAUI.ViewModels
{

    public partial class CutterCommandViewModel : BaseViewModel
    {
        IAlertService _alertService;
        SQLiteService _sqlLiteService;
        public CutterCommandViewModel(IAlertService alertService, SQLiteService sqlLiteService)
        {
            Title = "Cutter Commands";
            _alertService = alertService;
            _sqlLiteService = sqlLiteService;
            LoadSettings();
        }

        [ObservableProperty]
        string upCommand;

        [ObservableProperty]
        string downCommand;

        [ObservableProperty]
        string leftCommand;

        [ObservableProperty]
        string rightCommand;

        [RelayCommand]
        async Task Save()
        {
            if (await ValidateSettings())
            {

                _sqlLiteService.config.CutterUpCommand = UpCommand;
                _sqlLiteService.config.CutterDownCommand = DownCommand;
                _sqlLiteService.config.CutterLeftCommand = LeftCommand;
                _sqlLiteService.config.CutterRightCommand = RightCommand;
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
            if (string.IsNullOrWhiteSpace(UpCommand))
            {
                await _alertService.ShowAlertAsync("Error", $"Up Command Is Empty", "OK");
                return false;
            }
            if (string.IsNullOrWhiteSpace(DownCommand))
            {
                await _alertService.ShowAlertAsync("Error", $"Down Command Is Empty", "OK");
                return false;
            }
            if (string.IsNullOrWhiteSpace(LeftCommand))
            {
                await _alertService.ShowAlertAsync("Error", $"Left Command Is Empty", "OK");
                return false;
            }
            if (string.IsNullOrWhiteSpace(RightCommand))
            {
                await _alertService.ShowAlertAsync("Error", $"Right Command Is Empty", "OK");
                return false;
            }
            return true;

        }

        private void LoadSettings()
        {
            UpCommand = _sqlLiteService.config.CutterUpCommand;
            DownCommand = _sqlLiteService.config.CutterDownCommand;
            LeftCommand = _sqlLiteService.config.CutterLeftCommand;
            RightCommand = _sqlLiteService.config.CutterRightCommand;
        }
    }
}
