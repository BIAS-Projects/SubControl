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
    public partial class MainViewModel : BaseViewModel
    {
        SQLiteService _sqliteService;
        IAlertService _alertService;

        private readonly IMessenger _messenger;
        private readonly TcpSocketService _tcp;

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(IsNotConnected))]
        bool isConnected = false;

        static string _currentUnit = "mA";
        public bool IsNotConnected => !IsConnected;

        [ObservableProperty]
        private bool cutterRunning;


        public bool CanStart => IsConnected && !CutterRunning;

        public bool CanStop => IsConnected && CutterRunning;

        partial void OnIsConnectedChanged(bool value)
        {
            OnPropertyChanged(nameof(CanStart));
            OnPropertyChanged(nameof(CanStop));
        }

        partial void OnCutterRunningChanged(bool value)
        {
            OnPropertyChanged(nameof(CanStart));
            OnPropertyChanged(nameof(CanStop));
        }

        private double _sliderValue;
        public double SliderValue
        {
            get => _sliderValue;
            set
            {
                if (_sliderValue == value)
                    return;

                _sliderValue = value;
                OnPropertyChanged();

                OnSliderValueChanged(value);
            }
        }

        [ObservableProperty]
        private string status = "Disconnected";

        //[ObservableProperty]
        //private int cutterSpeed = 50;

        [ObservableProperty]
        private string cutterCurrent = $"0 {_currentUnit}";

        public MainViewModel(SQLiteService sqliteService, IAlertService alertService, IMessenger messenger, TcpSocketService tcp)
        {
            Title = "Cutter";
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
                    if(!IsConnected)
                    {
                        CutterRunning = false;
                    }
                });

            });


        }

        [RelayCommand]
        async Task Up()
        {
            Send(_sqliteService.config.CutterUpCommand);
        }
        [RelayCommand]
        async Task Down()
        {
            Send(_sqliteService.config.CutterDownCommand);
        }

        [RelayCommand]
        async Task Left()
        {
            Send(_sqliteService.config.CutterLeftCommand);
        }
        [RelayCommand]
        async Task Right()
        {
            Send(_sqliteService.config.CutterRightCommand);
        }

        public async Task ButtonLoaded()
        {
            if(_sqliteService.ConfigLoadedError)
            {
                await _alertService.ShowAlertAsync("Error", $"Failed To Load Configuration File, Failed To Load Default Settings, {_sqliteService.LastError}", "OK");
                return;
            }

            if (_sqliteService.DefaultsLoaded)
            {
                await _alertService.ShowAlertAsync("Warning", $"Failed To Load Configuration File, Restoring Default Settings", "OK");
                _sqliteService.DefaultsLoaded = false;
            }
        }

        public async Task GetConfig()
        {
            if (!await _sqliteService.GetConfigAsync())
            {
                if (await _sqliteService.SetDefaultConfig())
                {
                    _sqliteService.DefaultsLoaded = true;

                }

            }


        }


        [RelayCommand]
        public async Task Connect()
        {


            IsBusy = true;
            try
            {

                //if (!IPAddress.TryParse(_sqliteService.config.IPAddress, out IPAddress ip))
                //{
                //    await _alertService.ShowAlertAsync("Error", $"Saved IP Address Is Invalid", "OK");
                //    return;
                //}
                //int _port;
                //if (!int.TryParse(_sqliteService.config.Port, out _port))
                //{
                //    await _alertService.ShowAlertAsync("Error", $"Saved Port Is Invalid", "OK");
                //    return;
                //}
                //if (_port < 0 || _port > 65535)
                //{
                //    await _alertService.ShowAlertAsync("Error", $"Saved Port Is Out Of Range", "OK");
                //    return;
                //}
                //await _tcp.StartAsync("127.0.0.1", 9000);
                await _tcp.StartAsync(_sqliteService.config.IPAddress, Int32.Parse(_sqliteService.config.Port));
                IsBusy = false;
            }
            catch (Exception ex)
            {
                await _alertService.ShowAlertAsync("Error", $"{ex.Message}", "OK");
            }



        }

        [RelayCommand]
        public async Task Disconnect()
        {
            IsBusy = true;
            if(CutterRunning)
            {
                await _alertService.ShowAlertAsync("Warning", $"Turn Cutter Off Before Disconnecting", "OK");
                IsBusy = false;
                return;
            }

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



        [RelayCommand]
        public async Task CutterOn()
        {
            IsBusy = true;
            CutterRunning = true;
            Status = "Cutter Started";

            _messenger.Send(new TcpSendRequestMessage(Encoding.UTF8.GetBytes("START")));
            string speed = Math.Round(SliderValue).ToString();
            _messenger.Send(new TcpSendRequestMessage(Encoding.UTF8.GetBytes($"SPEED {speed}")));
            IsBusy = false;
        }


        [RelayCommand]
        public async Task CutterOff()
        {
            IsBusy = true;
            CutterRunning = false;
            Status = "Connected";
            _messenger.Send(new TcpSendRequestMessage(Encoding.UTF8.GetBytes("STOP")));
            IsBusy = false;
        }


        private void OnSliderValueChanged(double value)
        {
            if(!IsConnected)
                return;
            //change the speed to a whole interger value
            string speed = Math.Round(value).ToString();

            _messenger.Send(new TcpSendRequestMessage(Encoding.UTF8.GetBytes($"SPEED {speed}")));

        }





    }
}
