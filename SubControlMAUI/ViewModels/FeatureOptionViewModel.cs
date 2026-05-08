
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using Microsoft.Extensions.Logging;
using SubControlMAUI.Messages;
using SubControlMAUI.Models;
using SubControlMAUI.Services;
using SubControlMAUI.ViewModels;
using System.Collections.ObjectModel;
using System.Diagnostics;
using static System.Net.Mime.MediaTypeNames;

namespace SubControlMAUI.ViewModels;

public partial class FeatureOptionViewModel : BaseViewModel
{
    private IMessenger _messengerService;
    private ILogger<FeatureOptionViewModel> _loggerService;
    private INavigationService _navigationService;
    private IAlertService _alertService;
    private TcpSocketService _tcpService;
    private CancellationTokenSource _cts = new();

    private bool _isRebuilding = false;

    private readonly List<string> _allFunctions = new()
        { "Unselected", "TOM Input", "TOM Output", "TOM AHRS", "TOM GNSS", "TOM FLIR", "ROTOR" };

    public FeatureOptionViewModel(IMessenger messengerService,
        ILogger<FeatureOptionViewModel> loggerService,
        INavigationService navigationService, 
        IAlertService alertService,
        TcpSocketService tcpService)
    {

        _messengerService = messengerService;
        _loggerService = loggerService;
        _navigationService = navigationService;
        _alertService = alertService;
        _tcpService = tcpService;

        Title = "Feature to USB Mapping";

        UsbDevices.Add(new UsbDevice { Name = "USB1" });
        UsbDevices.Add(new UsbDevice { Name = "USB2" });
        UsbDevices.Add(new UsbDevice { Name = "USB3" });
        UsbDevices.Add(new UsbDevice { Name = "USB4" });
        UsbDevices.Add(new UsbDevice { Name = "USB5" });
        UsbDevices.Add(new UsbDevice { Name = "USB6" });

        foreach (var device in UsbDevices)
        {
            // Wire up callback instead of PropertyChanged
            device.OnFunctionSelected = HandleSelection;
            device.SetSelectionSilently("Unselected");
        }

        _messengerService.Register<TcpDataReceivedMessage>(this, (r, msg) =>
        {



            MainThread.BeginInvokeOnMainThread(async () =>
            {

                _alertService.ShowAlertAsync("Information", $"TcpDataReceivedMessage: {msg}", "OK");

            });

        });

        _messengerService.Register<TcpSendRequestMessage>(this, (r, msg) =>
        {
            //_alertService.ShowAlertAsync("Information", $"TcpSendRequestMessage: {msg}", "OK");

            MainThread.BeginInvokeOnMainThread(() =>
            {
                _alertService.ShowAlertAsync("Information", $"TcpSendRequestMessage: {msg}", "OK");
            });

        });

        _messengerService.Register<TcpStatusMessage>(this, (r, msg) =>
        {
            MainThread.BeginInvokeOnMainThread(() =>
            {
                StatusText = msg.Value;
            });

            

        });

        _messengerService.Register<TcpErrorMessage>(this, (r, msg) =>
        {

            // _alertService.ShowAlertAsync("Information", $"TcpErrorMessage: {msg.Value.Message}", "OK");
            _loggerService.LogError($"TcpErrorMessage : {msg}", msg);
            MainThread.BeginInvokeOnMainThread(() =>
            {
                StatusText = msg.Value.Message;

            });

        });



        _messengerService.Register<TcpAckTimeoutMessage>(this, (r, msg) =>
        {
            // _alertService.ShowAlertAsync("Information", $"TcpAckTimeoutMessage: {msg}", "OK");
            MainThread.BeginInvokeOnMainThread(() => 
            {
                StatusText = $"No response to: {msg.Command}";
            });
        });

        _messengerService.Register<TcpNackMessage>(this, (r, msg) =>
        {
         //   _alertService.ShowAlertAsync("Information", $"TcpNackMessage: {msg}", "OK");

            MainThread.BeginInvokeOnMainThread(() =>
            {
                StatusText = $"Server rejected '{msg.Command}': {msg.Reason}";
            });
        });


        _messengerService.Register<TcpIsConnected>(this, (r, msg) =>
        {
            MainThread.BeginInvokeOnMainThread(async () =>
            {
                //  await _alertService.ShowAlertAsync("Information", $"TcpIsConnected: {msg.Value}", "OK");
                if (!msg.Value)
                {
                    await Shell.Current.GoToAsync("//MainPage");
                }
            });
        });


    }
    [ObservableProperty]
    public bool usbDetailsDownloaded = false;

    [ObservableProperty]
    public string statusText = "";

    public ObservableCollection<UsbDevice> UsbDevices { get; set; } = new();

    private void HandleSelection(UsbDevice changedDevice, string selectedValue)
    {
        if (_isRebuilding) return;
        _isRebuilding = true;
        try
        {
            foreach (var device in UsbDevices)
            {
                var otherClaimed = UsbDevices
                    .Where(d => d != device && d.SelectedFunction != "Unselected")
                    .Select(d => d.SelectedFunction)
                    .ToHashSet();

                var available = _allFunctions
                    .Where(f => !otherClaimed.Contains(f))
                    .ToList();

                var currentSelection = device.SelectedFunction;

                // Update list first, then restore selection
                device.UpdateAvailableFunctions(available);
                device.SetSelectionSilently(
                    available.Contains(currentSelection) ? currentSelection : "Unselected");
            }
        }
        finally
        {
            _isRebuilding = false;
        }
    }

    [RelayCommand]
    public async Task QueryDevices() {
        IsBusy = true;
        try
        {

            IsBusy = true;
            //       var bytes = System.Text.Encoding.UTF8.GetBytes(text);

            TCPMessageBody<string> command = new TCPMessageBody<string>("TOM", "LIST REGISTERED", "");
            await _tcpService.SendCommandAsync(command, _cts.Token);
            IsBusy = false;

            UsbDetailsDownloaded = true;
            StatusText = "Downloaded";
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    public void Save() { }

    private async Task GoBack()
    {
        await _navigationService.GoToAsync("..");
    }
}