
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using Microsoft.Extensions.Logging;
using SubConsole.Models;
using SubControlMAUI.Messages;
using SubControlMAUI.Models;
using SubControlMAUI.Services;
using System;
using System.Collections.Generic;
using System.Net;
using System.Text;
using System.Text.Json;


namespace SubControlMAUI.ViewModels
{
    public partial class PeriscopeViewModel : BaseViewModel
    {

    
        SQLiteService _sqliteService;
        IAlertService _alertService;
        ILogger<MainViewModel> _logger;

        private readonly IMessenger _messenger;
        private readonly TcpSocketService _tcp;



        [ObservableProperty]
        private double buttonSize;

        [ObservableProperty]
        private double layoutSpacing;

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(IsNotConnected))]
        bool isConnected = false;

        static string _currentUnit = "mA";
        public bool IsNotConnected => !IsConnected;

        [ObservableProperty]
        private bool cutterRunning;
        [ObservableProperty]
        private List<UsbSerialPortInfo> usbSerialPortInfoList = new List<UsbSerialPortInfo>();


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

        //private double _sliderValue;
        //public double SliderValue
        //{
        //    get => _sliderValue;
        //    set
        //    {
        //        if (_sliderValue == value)
        //            return;

        //        _sliderValue = value;
        //        OnPropertyChanged();

        //        OnSliderValueChanged(value);
        //    }
        //}

        [ObservableProperty]
        private string status = "Disconnected";

        //[ObservableProperty]
        //private int cutterSpeed = 50;

        //[ObservableProperty]
        //private string cutterCurrent = $"0 {_currentUnit}";

        public PeriscopeViewModel(SQLiteService sqliteService, IAlertService alertService, IMessenger messenger, TcpSocketService tcp, ILogger<MainViewModel> logger) : base(messenger,logger)
        {
            Title = "Periscope";
            _sqliteService = sqliteService;
            _alertService = alertService;
            _logger = logger;

            _messenger = messenger;
            _tcp = tcp;

            Status = "Disconnected";






            _messenger.Register<TcpDataReceivedMessage>(this, (r, msg) =>
            {
                MainThread.BeginInvokeOnMainThread(async () =>
                {
                    //string message = Encoding.UTF8.GetString(msg.Value);
                    //if (!await HandleTcpReceivedMessage(message))
                    //{
                    //    Status = "Error processing Command: " + message;
                    //}
                    //else
                    //{
                    //    Status = "Success processing Command: " + message;
                    //}


                });

            });

            _messenger.Register<TcpSendRequestMessage>(this, (r, msg) =>
            {
                MainThread.BeginInvokeOnMainThread(() =>
                {
              //      Status = Encoding.UTF8.GetString(msg.Value);
                });

            });

            _messenger.Register<TcpStatusMessage>(this, (r, msg) =>
            {
                MainThread.BeginInvokeOnMainThread(() =>
                {
                    Status = msg.Value;
                });

            });

            _messenger.Register<TcpErrorMessage>(this, (r, msg) =>
            {
                _logger?.LogError($"TcpErrorMessage : {msg}", msg);
                MainThread.BeginInvokeOnMainThread(() =>
                {
                    Status = msg.Value.Message;

                });

            });



            _messenger.Register<TcpAckTimeoutMessage>(this, (r, msg) =>
            {

                MainThread.BeginInvokeOnMainThread(() =>
                    Status = $"No response to: {msg.Command}");
            });

            _messenger.Register<TcpNackMessage>(this, (r, msg) =>
            {
                MainThread.BeginInvokeOnMainThread(() =>
                    Status = $"Server rejected '{msg.Command}': {msg.Reason}");
            });


            _messenger.Register<TcpIsConnected>(this, (r, msg) =>
            {

                MainThread.BeginInvokeOnMainThread(() =>
                {
                    IsConnected = msg.Value;
                    if (!IsConnected)
                    {
                        CutterRunning = false;
                    }
                });

            });


        }


        [RelayCommand]
        async Task StartTOM()
        {
            Send("START TOM ALL");
        }

        [RelayCommand]
        async Task StopTOM()
        {
            Send("STOP TOM ALL");
        }

        [RelayCommand]
        async Task FLIRWhitehot()
        {
            Send("FLIR WHITEHOT");
        }

        [RelayCommand]
        async Task FLIRRainbow()
        {
            Send("FLIR RAINBOW");
        }

        [RelayCommand]
        async Task GetAllUSBPorts()
        {
            Send("GET USB PORTS");
        }

        [RelayCommand]
        async Task GetVideoPorts()
        {
            Send("GET VIDEO PORTS");
        }

        [RelayCommand]
        async Task RotorForward()
        {
            Send("ROTOR FORWARD");
        }
        [RelayCommand]
        async Task RotorBackward()
        {
            Send("ROTOR BACKWARD");
        }
        [RelayCommand]
        async Task RotorStop()
        {
            Send("ROTOR STOP");
        }
        [RelayCommand]
        async Task Right()
        {
            Send(_sqliteService.config.CutterRightCommand);
        }

        public async Task ButtonLoaded()
        {
            if (_sqliteService.ConfigLoadedError)
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
            if (CutterRunning)
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
           // _messenger.Send(new TcpSendRequestMessage(bytes));
            IsBusy = false;
        }



        //[RelayCommand]
        //public async Task CutterOn()
        //{
        //    IsBusy = true;
        //    CutterRunning = true;
        //    Status = "Cutter Started";

        //    _messenger.Send(new TcpSendRequestMessage(Encoding.UTF8.GetBytes("START")));
        //    string speed = Math.Round(SliderValue).ToString();
        //    _messenger.Send(new TcpSendRequestMessage(Encoding.UTF8.GetBytes($"SPEED {speed}")));
        //    IsBusy = false;
        //}


        //[RelayCommand]
        //public async Task CutterOff()
        //{
        //    IsBusy = true;
        //    CutterRunning = false;
        //    Status = "Connected";
        //    _messenger.Send(new TcpSendRequestMessage(Encoding.UTF8.GetBytes("STOP")));
        //    IsBusy = false;
        //}






        private void OnSliderValueChanged(double value)
        {
            if (!IsConnected)
                return;
            //change the speed to a whole interger value
            string speed = Math.Round(value).ToString();

      //      _messenger.Send(new TcpSendRequestMessage(Encoding.UTF8.GetBytes($"SPEED {speed}")));

        }


        public async Task<bool> HandleTcpReceivedMessage(string message)
        {
            if (string.IsNullOrEmpty(message))
            { return false; }
            var command = message.Split(TcpProtocol.CommandSeparatorChar);
            if (command.Length <= 1)
            { return false; }
            switch (command[0])
            {
                case "GET USBCOMMPORTS":
                    try
                    {
                        UsbSerialPortInfoList.Clear();
                        int index = message.IndexOf(',');

                        if (index == -1)
                        {
                            return false;
                        }
                        string result = message.Substring(++index);

                        var ports = JsonSerializer.Deserialize<List<UsbSerialPortInfo>>(result);
                        return true;
                    }
                    catch (Exception ex)
                    {
                        _logger?.LogError($"Command : {command} Message: {message} ", command, message);
                        return false;

                    }


                case "GET FEATURES":
                    // TODO: return feature flags
                    return true;

                case "START TOM ALL":
                    return true;

                case "STOP TOM ALL":
                    // TODO: implement TOM shutdown sequence
                    return true;

                default: return false;
            }
        }

        private static IEnumerable<string> ParseTemplates(string source)
        {
            if (source is null)
            {
                throw new ArgumentNullException(nameof(source)); //Or an empty enumerable.
            }
            var result = new List<string>();
            int currentIdx = 0;
            while ((currentIdx = source.IndexOf('{', currentIdx)) > -1)
            {
                int closingIdx = source.IndexOf('}', currentIdx);
                if (closingIdx < 0)
                {
                    throw new InvalidOperationException($"Parsing failed, no closing brace for the opening brace found at: {currentIdx}");
                }
                result.Add(source.Substring(currentIdx, closingIdx - currentIdx + 1));
                currentIdx = closingIdx;
            }
            return result;
        }



    }
}
