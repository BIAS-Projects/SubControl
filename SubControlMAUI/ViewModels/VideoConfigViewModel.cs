using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using Microsoft.Extensions.Logging;
using SubControlMAUI.Models;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Text;

namespace SubControlMAUI.ViewModels
{
    public partial class VideoConfigViewModel : BaseViewModel
    {
        
        private bool _isRebuilding = false;

        private readonly List<string> _allFunctions = new()
        {"Unselected", "Video", "FLIR"};

        IMessenger _messenger;
        ILogger<VideoConfigViewModel> _logger;

        public VideoConfigViewModel(IMessenger messenger,
        ILogger<VideoConfigViewModel> logger)
        {
            Title = "Feature to USB Mapping";

            _messenger = messenger;
            _logger = logger;

            VideoDevices.Add(new VideoDevice { Name = "USB1" });
            VideoDevices.Add(new VideoDevice { Name = "USB2" });

            foreach (var device in VideoDevices)
            {
                // Wire up callback instead of PropertyChanged
                device.OnFunctionSelected = HandleSelection;
                device.SetSelectionSilently("Unselected");
            }
        }

        [ObservableProperty]
        public bool dataUploaded = false;

        public ObservableCollection<VideoDevice> VideoDevices { get; set; } = new();

        private void HandleSelection(VideoDevice changedDevice, string selectedValue)
        {
            if (_isRebuilding) return;
            _isRebuilding = true;
            try
            {
                foreach (var device in VideoDevices)
                {
                    var otherClaimed = VideoDevices
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
}
