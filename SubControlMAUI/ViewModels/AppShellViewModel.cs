using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Messaging;
using SubControlMAUI.Messages;
using SubControlMAUI.Services;
using System;
using System.Collections.Generic;
using System.Text;

namespace SubControlMAUI.ViewModels
{
    public partial class AppShellViewModel : BaseViewModel
    {
        SQLiteService _sqliteService;
        IMessenger _messenger;

        [ObservableProperty]
        private bool cutterEnabled = true;

        [ObservableProperty]
        private bool periscopeEnabled = true;

        public AppShellViewModel(SQLiteService sqliteService, IMessenger messenger)
        {
            Title = "SubControlMAUI";
            _sqliteService = sqliteService;
            _messenger = messenger;

            _messenger.Register<FeatureUpdateMessage>(this, (r, m) =>
            {
                MainThread.BeginInvokeOnMainThread(() =>
                {
                    CutterEnabled = _sqliteService.config.Features.Contains("CUTTER");
                    PeriscopeEnabled = _sqliteService.config.Features.Contains("PERISCOPE");
                });

            });

        }

        public async Task GetConfig()
        {
            if (!await _sqliteService.GetConfigAsync())
            {
                _sqliteService.DefaultsLoaded = true;
                if (!await _sqliteService.SetDefaultConfig())
                {
                    _sqliteService.ConfigLoadedError = true;

                }

            }
            CutterEnabled = _sqliteService.config.Features.Contains("CUTTER");
            PeriscopeEnabled = _sqliteService.config.Features.Contains("PERISCOPE");    
            //else
            //{
            //    _sqliteService.ConfigLoadedError = false;
            //}


        }
    }
}
