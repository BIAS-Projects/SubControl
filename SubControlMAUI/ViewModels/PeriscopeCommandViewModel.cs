using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using Microsoft.Extensions.Logging;
using SubControlMAUI.Services;
using System;
using System.Collections.Generic;
using System.Text;

namespace SubControlMAUI.ViewModels
{
    public partial class PeriscopeCommandViewModel : BaseViewModel
    {
        IAlertService _alertService;
        SQLiteService _sqlLiteService;
        IMessenger _messenger;
        ILogger<PeriscopeCommandViewModel> _logger;

        public PeriscopeCommandViewModel(IAlertService alertService, SQLiteService sqlLiteService, IMessenger messenger,
        ILogger<PeriscopeCommandViewModel> logger)
        {
            Title = "Periscope Commands";
            _alertService = alertService;
            _sqlLiteService = sqlLiteService;
            _messenger = messenger;
            _logger = logger;
            LoadSettings();
        }

        //[ObservableProperty]
        //string upCommand;

        //[ObservableProperty]
        //string downCommand;

        //[ObservableProperty]
        //string leftCommand;

        //[ObservableProperty]
        //string rightCommand;

        [RelayCommand]
        async Task connectToVideo()
        {

        }

        //[RelayCommand]
        //async Task connectToVideo()
        //{

        //}






        //private async Task<bool> ValidateSettings()
        //{
        //    if (string.IsNullOrWhiteSpace(UpCommand))
        //    {
        //        await _alertService.ShowAlertAsync("Error", $"Up Command Is Empty", "OK");
        //        return false;
        //    }
        //    if (string.IsNullOrWhiteSpace(DownCommand))
        //    {
        //        await _alertService.ShowAlertAsync("Error", $"Down Command Is Empty", "OK");
        //        return false;
        //    }

        //    return true;

        //}

        private void LoadSettings()
        {
            //UpCommand = _sqlLiteService.config.PeriscopeUpCommand;
            //DownCommand = _sqlLiteService.config.PeriscopeDownCommand;
            //LeftCommand = _sqlLiteService.config.CutterLeftCommand;
            //RightCommand = _sqlLiteService.config.CutterRightCommand;
        }
    }
}
