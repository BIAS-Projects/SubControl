using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using Microsoft.Extensions.Logging;
using SubControlMAUI.Models;
using SubControlMAUI.ViewModels;
using System.Collections.ObjectModel;
using System.Diagnostics;

namespace SubControlMAUI.ViewModels;

public partial class FeatureOptionViewModel : BaseViewModel
{
    private bool _isRebuilding = false;

    private readonly List<string> _allFunctions = new()
        { "Unselected", "TOM Input", "TOM Output", "TOM AHRS", "TOM GNSS", "TOM FLIR", "ROTOR" };

    public FeatureOptionViewModel(IMessenger messenger,
        ILogger<PeriscopeViewModel> logger): base(messenger, logger)
    {
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
    }

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
    public void Save() { }

    [RelayCommand]
    public void QueryDevices() { }
}