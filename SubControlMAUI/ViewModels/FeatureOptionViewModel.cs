

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
using System.Text.Json;
using static SubControlMAUI.Models.UsbDeviceInfo;
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
    [ObservableProperty]
    public ObservableCollection<UsbSerialPortInfo> availablePorts = new();

    //private readonly List<string> _allFunctions = new()
    //    { "Unselected", "TOM Input", "TOM Output", "TOM AHRS", "TOM GNSS", "TOM FLIR", "ROTOR" };

    private readonly List<FunctionToPortEntry> _allFunctions = new()
{
    new FunctionToPortEntry { FunctionName = "TOM Input",  BaudRate = "115200", WorkerType = SerialWorkerType.Text.ToString() },
    new FunctionToPortEntry { FunctionName = "TOM Output", BaudRate = "115200", WorkerType = SerialWorkerType.Text.ToString() },
    new FunctionToPortEntry { FunctionName = "TOM AHRS",   BaudRate = "115200", WorkerType = SerialWorkerType.Text.ToString() },
    new FunctionToPortEntry { FunctionName = "TOM GNSS",   BaudRate = "115200", WorkerType = SerialWorkerType.Text.ToString() },
    new FunctionToPortEntry { FunctionName = "TOM FLIR",   BaudRate = "921600", WorkerType = SerialWorkerType.Flir.ToString() },
    new FunctionToPortEntry { FunctionName = "ROTOR",      BaudRate = "115200", WorkerType = SerialWorkerType.Text.ToString() },
};

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


        foreach (var device in UsbDevices)
        {
            // Wire up callback instead of PropertyChanged
            device.OnFunctionSelected = HandleSelection;
            device.SetSelectionSilently("Unselected");
        }



        // Replace the messenger registration for TcpDataReceivedMessage:
        _messengerService.Register<TcpDataReceivedMessage>(this, async (r, msg) =>
        {
            if (!msg.Value.Function.Equals(nameof(FeatureOptionViewModel))) return;

            if (msg.Value.Command == "LIST DEVICES")
            {
                await HandleListDevicesResponseAsync(msg.Value.Data);
                return;
            }

            if (msg.Value.Command == "LIST REGISTERED")
            {
                await HandleListRegisteredResponseAsync(msg.Value.Data);
                return;
            }

            if (msg.Value.Command == "REGISTER")
            {
                await HandleRegisterResponseAsync(msg.Value.Data);
                return;
            }

            if (msg.Value.Command == "UNREGISTER")
            {
                await HandleUnregisterResponseAsync(msg.Value.Data);
                return;
            }




            await _alertService.ShowAlertAsync("Information", $"TcpDataReceivedMessage: {msg.Value}", "OK");
        });

        _messengerService.Register<TcpSendRequestMessage>(this, async (r, msg) =>
        {
            //_alertService.ShowAlertAsync("Information", $"TcpSendRequestMessage: {msg}", "OK");

         //   await MainThread.InvokeOnMainThreadAsync(async () =>
         //   {
                await _alertService.ShowAlertAsync("Information", $"TcpSendRequestMessage: {msg.Value}", "OK");
         //   });

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
                    .Select(f => f.FunctionName)
                    .Where(f => !otherClaimed.Contains(f))
                    .Prepend("Unselected")
                    .ToList();

                var currentSelection = device.SelectedFunction;

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

    //[RelayCommand]
    //public async Task QueryDevices() {
    //    IsBusy = true;
    //    try
    //    {

    //        IsBusy = true;
    //        //       var bytes = System.Text.Encoding.UTF8.GetBytes(text);

    //        TCPMessageBody<string> command = new TCPMessageBody<string>(nameof(FeatureOptionViewModel), "LIST DEVICES", "");
    //        if(await _tcpService.SendCommandAsync(command, _cts.Token))
    //        {
    //            UsbDetailsDownloaded = true;
    //            StatusText = "Downloaded";

    //        }
    //        else
    //        {
    //            StatusText = "Error";
    //        }



    //    }
    //    finally
    //    {
    //        IsBusy = false;
    //    }
    //}

    [RelayCommand]
    public async Task Save()
    {
        IsBusy = true;
        try
        {
            foreach (var device in UsbDevices)
            {
                if (device.SelectedFunction == "Unselected")
                {
                    // Skip slots that were never populated with a real port
                    if (device.Name.StartsWith("USB") && device.Description is null) continue;

                    var command = new TCPMessageBody<string>(
                        nameof(FeatureOptionViewModel), "UNREGISTER", device.Name);

                    if (!await _tcpService.SendCommandAsync(command, _cts.Token))
                    {
                        StatusText = $"UNREGISTER failed for {device.Name}";
                        return;
                    }
                }
                else
                {
                    var template = _allFunctions.First(f => f.FunctionName == device.SelectedFunction);

                    var entry = new FunctionToPortEntry
                    {
                        DeviceKey = device.Name,
                        FunctionName = device.SelectedFunction,
                        BaudRate = template.BaudRate,
                        WorkerType = template.WorkerType
                    };

                    var command = new TCPMessageBody<string>(
                        nameof(FeatureOptionViewModel), "REGISTER",
                        JsonSerializer.Serialize(entry));

                    if (!await _tcpService.SendCommandAsync(command, _cts.Token))
                    {
                        StatusText = $"REGISTER failed for {device.Name} → {device.SelectedFunction}";
                        return;
                    }
                }
            }

            StatusText = "Save complete";
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task GoBack()
    {
        await _navigationService.GoToAsync("..");
    }

    private async Task HandleListDevicesResponseAsync(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            await MainThread.InvokeOnMainThreadAsync(() =>
                StatusText = "Empty response from server");
            return;
        }

        try
        {
            var response = JsonSerializer.Deserialize<ListDevicesResponse>(json,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

            if (response is null || !response.Ok)
            {
                await MainThread.InvokeOnMainThreadAsync(() =>
                    StatusText = $"Server error: {response?.Error ?? "unknown"}");
                return;
            }

            await MainThread.InvokeOnMainThreadAsync(() =>
            {
                // 1. Update the raw ports collection
                AvailablePorts.Clear();
                foreach (var port in response.Data)
                    AvailablePorts.Add(port);

                _isRebuilding = true;
                try
                {
                    // 2. For each received port, find an existing UsbDevice by Key or add a new one
                    for (int i = 0; i < response.Data.Count; i++)
                    {
                        var port = response.Data[i];

                        // Try to find an existing slot that already represents this port
                        var existing = UsbDevices.FirstOrDefault(d => d.Name == port.Key);

                        if (existing is not null)
                        {
                            // Already present — just refresh description and reset selection
                            existing.Description = port.Description;
                            existing.SetSelectionSilently("Unselected");
                        }
                        else if (i < UsbDevices.Count)
                        {
                            // Reuse an existing slot at this index position
                            UsbDevices[i].Name = port.Key;
                            UsbDevices[i].Description = port.Description;
                            UsbDevices[i].SetSelectionSilently("Unselected");
                        }
                        else
                        {
                            // No slot available — create and add a new UsbDevice
                            var newDevice = new UsbDevice
                            {
                                Name = port.Key,
                                Description = port.Description,
                                OnFunctionSelected = HandleSelection
                            };
                            newDevice.SetSelectionSilently("Unselected");
                            UsbDevices.Add(newDevice);
                        }
                    }

                    // 3. Reset any slots beyond the received port count back to defaults
                    for (int i = response.Data.Count; i < UsbDevices.Count; i++)
                    {
                        UsbDevices[i].Name = $"USB{i + 1}";
                        UsbDevices[i].Description = null;
                        UsbDevices[i].SetSelectionSilently("Unselected");
                    }
                }
                finally
                {
                    _isRebuilding = false;
                }

                UsbDetailsDownloaded = true;
                StatusText = $"Downloaded {response.Data.Count} port(s)";
            });

            // Chain: now request what's already registered so selections can be restored
            var command = new TCPMessageBody<string>(
                nameof(FeatureOptionViewModel), "LIST REGISTERED", "");

            if (!await _tcpService.SendCommandAsync(command, _cts.Token))
            {
                await MainThread.InvokeOnMainThreadAsync(() =>
                    StatusText = "LIST REGISTERED request failed");
            }
        }
        catch (JsonException ex)
        {
            _loggerService.LogError(ex, "Failed to deserialize LIST DEVICES response");
            await MainThread.InvokeOnMainThreadAsync(() =>
                StatusText = $"Parse error: {ex.Message}");
        }
    }

    private async Task HandleListRegisteredResponseAsync(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            await MainThread.InvokeOnMainThreadAsync(() =>
                StatusText = "Empty LIST REGISTERED response");
            return;
        }

        try
        {
            var response = JsonSerializer.Deserialize<ListRegisteredResponse>(json,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

            if (response is null || !response.Ok)
            {
                await MainThread.InvokeOnMainThreadAsync(() =>
                    StatusText = $"LIST REGISTERED error: {response?.Error ?? "unknown"}");
                return;
            }

            await MainThread.InvokeOnMainThreadAsync(() =>
            {
                _isRebuilding = true;
                try
                {
                    foreach (var entry in response.Data)
                    {
                        var device = UsbDevices.FirstOrDefault(d => d.Name == entry.Key);
                        if (device is null) continue;

                        if (!string.IsNullOrWhiteSpace(entry.FunctionName)
                            && _allFunctions.Any(f => f.FunctionName == entry.FunctionName))
                        {
                            device.SetSelectionSilently(entry.FunctionName);
                        }
                    }
                }
                finally
                {
                    _isRebuilding = false;
                }

                // Rebuild available-function lists to enforce exclusivity across all devices
                if (UsbDevices.Any())
                    HandleSelection(UsbDevices[0], UsbDevices[0].SelectedFunction);

                StatusText = $"Restored {response.Data.Count} registration(s)";
            });
        }
        catch (JsonException ex)
        {
            _loggerService.LogError(ex, "Failed to deserialize LIST REGISTERED response");
            await MainThread.InvokeOnMainThreadAsync(() =>
                StatusText = $"Parse error: {ex.Message}");
        }
    }

    private async Task HandleRegisterResponseAsync(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            await MainThread.InvokeOnMainThreadAsync(() =>
                StatusText = "Empty REGISTER response");
            return;
        }

        try
        {
            var response = JsonSerializer.Deserialize<CommandResponse>(json,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

            await MainThread.InvokeOnMainThreadAsync(() =>
                StatusText = response?.Ok == true
                    ? "Registered successfully"
                    : $"REGISTER error: {response?.Error ?? "unknown"}");
        }
        catch (JsonException ex)
        {
            _loggerService.LogError(ex, "Failed to deserialize REGISTER response");
            await MainThread.InvokeOnMainThreadAsync(() =>
                StatusText = $"Parse error: {ex.Message}");
        }
    }

    private async Task HandleUnregisterResponseAsync(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            await MainThread.InvokeOnMainThreadAsync(() =>
                StatusText = "Empty UNREGISTER response");
            return;
        }

        try
        {
            var response = JsonSerializer.Deserialize<CommandResponse>(json,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

            await MainThread.InvokeOnMainThreadAsync(() =>
                StatusText = response?.Ok == true
                    ? "Unregistered successfully"
                    : $"UNREGISTER error: {response?.Error ?? "unknown"}");
        }
        catch (JsonException ex)
        {
            _loggerService.LogError(ex, "Failed to deserialize UNREGISTER response");
            await MainThread.InvokeOnMainThreadAsync(() =>
                StatusText = $"Parse error: {ex.Message}");
        }
    }



    [RelayCommand]
    public async Task QueryDevices()
    {
        IsBusy = true;
        try
        {
            var command = new TCPMessageBody<string>(
                nameof(FeatureOptionViewModel), "LIST DEVICES", "");

            if (!await _tcpService.SendCommandAsync(command, _cts.Token))
            {
                StatusText = "Request failed — no response from server";
                UsbDetailsDownloaded = false;
                return;
            }
            // LIST DEVICES success is handled in TcpDataReceivedMessage → HandleListDevicesResponseAsync
            // which will chain LIST REGISTERED via QueryRegistered()
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    public async Task QueryRegistered()
    {
        var command = new TCPMessageBody<string>(
            nameof(FeatureOptionViewModel), "LIST REGISTERED", "");

        if (!await _tcpService.SendCommandAsync(command, _cts.Token))
            StatusText = "LIST REGISTERED failed — no response from server";
    }


}