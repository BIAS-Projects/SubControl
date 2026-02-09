
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using SubControlMAUI.Messages;
using SubControlMAUI.Services;
using System;
using System.Collections.Generic;
using System.Text;

namespace SubControlMAUI.ViewModels
{
    public partial class FeatureOptionViewModel : BaseViewModel
    {
        SQLiteService _sqlLiteService;
        IAlertService _alertService;
        private readonly IMessenger _messenger;
        public FeatureOptionViewModel(SQLiteService sqlLiteService, IAlertService alertService, IMessenger messenger)
        {
            Title = "Feature Options";
            _sqlLiteService = sqlLiteService;
            _alertService = alertService;
            _messenger = messenger;
            LoadSettings();
        }

        [ObservableProperty]
        bool cutterEnabled;

        [ObservableProperty]
        bool periscopeEnabled;

        [RelayCommand]
        public async Task Save()
        {
            string featureSettings = string.Empty;
            if(CutterEnabled)
                featureSettings += "CUTTER,";
            if(PeriscopeEnabled)
                featureSettings += "PERISCOPE,";
            _sqlLiteService.config.Features = featureSettings.TrimEnd(','); 
            if (await _sqlLiteService.SaveConfigAsync(true) == 1)
            {
                _messenger.Send(new FeatureUpdateMessage("Update"));
                await _alertService.ShowAlertAsync("Informarion", $"Settings Saved Successfully", "OK");
            }
            else
            {
                await _alertService.ShowAlertAsync("Error", $"Error Saving Settings: {_sqlLiteService.LastError}", "OK");
            }
        }

        private void LoadSettings()
        {
            CutterEnabled = _sqlLiteService.config.Features.Contains("CUTTER");
            PeriscopeEnabled = _sqlLiteService.config.Features.Contains("PERISCOPE");
        }

    }
}
