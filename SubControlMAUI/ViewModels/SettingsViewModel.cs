using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using SubControlMAUI.Messages;
using SubControlMAUI.Model;
using SubControlMAUI.Services;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Net;
using System.Text;

namespace SubControlMAUI.ViewModels
{
    public partial class SettingsViewModel : BaseViewModel
    {
        SQLiteService _sqliteService;
        IAlertService _alertService;
        TcpSocketService _tCPService;
        IMessenger _messenger;

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(IsNotConnected))]
        bool isConnected = false;

        public bool IsNotConnected => !IsConnected;

        public SettingsViewModel(SQLiteService sQLiteService, IAlertService alertService, TcpSocketService tCPService, IMessenger messenger)
        {
            Title = "Settings";
            _sqliteService = sQLiteService;
            _alertService = alertService;
            _messenger = messenger;
            Setup();
            _tCPService = tCPService;


            _messenger.Register<TcpIsConnected>(this, (r, m) =>
            {

                MainThread.BeginInvokeOnMainThread(() =>
                {
                    IsConnected = m.Value;

                });

            });
        }




        [ObservableProperty]
        string inputIPAddress;

        [ObservableProperty]
        string port;

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
            if (! await ValidateSettings())
            {
                await _alertService.ShowAlertAsync("Error", $"Settings Input Is Incorrect", "OK");
                return;
            }
            try
            {

                int result = await _sqliteService.SaveConfigAsync(new Config
                {
                    Id = 0,
                    IPAddress = InputIPAddress,
                    Port = Port,
                    UpCommand = UpCommand,
                    DownCommand = DownCommand,
                    LeftCommand = LeftCommand,
                    RightCommand = RightCommand
                }, true);
                if (result != 1)
                {
                    await _alertService.ShowAlertAsync("Error", $"Error Loading Settings - Settings Not Saved", "OK");
                }
                else
                {
                    await _alertService.ShowAlertAsync("Success", $"Settings Saved", "OK");

                }
            }
            catch (Exception ex)
            {
                await _alertService.ShowAlertAsync("Error", $"Error Loading Settings - {ex.Message}", "OK");
                return;
            }
        }

        [RelayCommand]
        async Task Test()
        {

            
          //      await _tCPService.SendAsync("Test Command");
            //await _tCPService.SendMessageFromIPAddressAndPort(IPAddress, Port, "Test Command");

        //    await _alertService.ShowAlertAsync("Info", $"Command Sent", "OK");


        }

        [RelayCommand]
        async Task ListenerStart()
        {
        //    await _tCPService.StartAsync(IPAddress, Port);
            //  await _tCPService.StartTestIPAddressAndPort(IPAddress, Port);

            //     await _tCPService.StartListenerFromIPAddressAndPort(IPAddress, Port);

            //    await _alertService.ShowAlertAsync("Info", $"Listener Started", "OK");
        }

        [RelayCommand]
        async Task ListenerStop()
        {
            // _tCPService.StopListener();

          //  _tCPService.StopAsync();

            await _alertService.ShowAlertAsync("Info", $"Listener Stopped", "OK");
        }

        private async Task<bool> ValidateSettings()
        {
            if (string.IsNullOrWhiteSpace(InputIPAddress))
            {
                await _alertService.ShowAlertAsync("Error", $"IP Address Is Empty", "OK");
                return false;
            }
            if (string.IsNullOrWhiteSpace(Port))
            {
                await _alertService.ShowAlertAsync("Error", $"Port Is Empty", "OK");
                return false;
            }
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

            if (!IPAddress.TryParse(InputIPAddress, out IPAddress ip))
            {
                await _alertService.ShowAlertAsync("Error", $"IP Address Is Invalid", "OK");
                return false;
            }
            int _port;
            if (!int.TryParse(_sqliteService.config.Port, out _port))
            {
                await _alertService.ShowAlertAsync("Error", $"Saved Port Is Invalid", "OK");
                return false;
            }
            if (_port < 0 || _port > 65535)
            {
                await _alertService.ShowAlertAsync("Error", $"Saved Port Is Out Of Range", "OK");
                return false;
            }

            return true;

        }



        public void Setup()
        {
            InputIPAddress = _sqliteService.config.IPAddress;
            Port = _sqliteService.config.Port;
            UpCommand = _sqliteService.config.UpCommand;
            DownCommand = _sqliteService.config.DownCommand;
            LeftCommand = _sqliteService.config.LeftCommand;
            RightCommand = _sqliteService.config.RightCommand;
        }

    }
}
