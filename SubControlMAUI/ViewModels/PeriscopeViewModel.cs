
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using SubControlMAUI.Messages;
using SubControlMAUI.Services;
using System;
using System.Collections.Generic;
using System.Net;
using System.Text;


namespace SubControlMAUI.ViewModels
{
    public partial class PeriscopeViewModel : BaseViewModel
    {

        SQLiteService _sqliteService;
        IAlertService _alertService;

        private readonly IMessenger _messenger;
        private readonly TcpSocketService _tcp;

        static string _currentUnit = "mA";

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(IsNotConnected))]
        bool isConnected = false;

        public bool IsNotConnected => !IsConnected;

        //partial void OnIsConnectedChanged(bool value)
        //{
        //    OnPropertyChanged(nameof(CanStart));
        //    OnPropertyChanged(nameof(CanStop));
        //}

        [ObservableProperty]
        private string cutterCurrent = $"0 {_currentUnit}";

        [ObservableProperty]
        private string status = "Disconnected";

        public PeriscopeViewModel(SQLiteService sqliteService, IAlertService alertService, IMessenger messenger, TcpSocketService tcp)
        {
            Title = "Periscope";
            _sqliteService = sqliteService;
            _alertService = alertService;

            _messenger = messenger;
            _tcp = tcp;

            _messenger.Register<TcpDataReceivedMessage>(this, (r, m) =>
            {
                MainThread.BeginInvokeOnMainThread(() =>
                {
                    string message = Encoding.UTF8.GetString(m.Value);

                    string[] words = message.Split(' ');
                    if ((words[0] == "CURRENT"))
                    {
                        CutterCurrent = $"{words[1]} {_currentUnit}";
                    }
                    else
                    {
                        Status = Encoding.UTF8.GetString(m.Value);
                    }



                });

            });

            _messenger.Register<TcpSendRequestMessage>(this, (r, m) =>
            {
                MainThread.BeginInvokeOnMainThread(() =>
                {
                    Status = Encoding.UTF8.GetString(m.Value);
                });

            });

            _messenger.Register<TcpStatusMessage>(this, (r, m) =>
            {
                MainThread.BeginInvokeOnMainThread(() =>
                {
                    Status = m.Value;
                });

            });

            _messenger.Register<TcpErrorMessage>(this, (r, m) =>
            {
                MainThread.BeginInvokeOnMainThread(() =>
                {
                    Status = m.Value.Message;
                });

            });

            _messenger.Register<TcpIsConnected>(this, (r, m) =>
            {

                MainThread.BeginInvokeOnMainThread(() =>
                {
                    IsConnected = m.Value;
                });

            });
        }

        [RelayCommand]
        async Task Up()
        {
            Send(_sqliteService.config.PeriscopeUpCommand);
        }
        [RelayCommand]
        async Task Down()
        {
            Send(_sqliteService.config.PeriscopeDownCommand);
        }

        [RelayCommand]
        public async Task Connect()
        {


            IsBusy = true;
            try
            {

                await _tcp.StartAsync(_sqliteService.config.IPAddress, Int32.Parse(_sqliteService.config.Port));

            }
            catch (Exception ex)
            {
                await _alertService.ShowAlertAsync("Error", $"{ex.Message}", "OK");
            }
            finally
            {
                IsBusy = false;
            }



        }

        [RelayCommand]
        public async Task Disconnect()
        {
            IsBusy = true;

            await _tcp.StopAsync();
            IsBusy = false;
        }

        public void Send(string text)
        {
            IsBusy = true;
            var bytes = System.Text.Encoding.UTF8.GetBytes(text);
            _messenger.Send(new TcpSendRequestMessage(bytes));
            IsBusy = false;
        }


    }
}
