
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using Microsoft.Extensions.Logging;
using SubControlMAUI.Models;
using SubControlMAUI.Services;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace SubControlMAUI.ViewModels
{
    public partial class RotatorOptionsViewModel : BaseViewModel
    {
        private readonly IMessenger _messenger;
        private readonly ILogger<RotatorOptionsViewModel> _logger;
        private readonly ApplicationStateService _applicationStateService;
        private SQLiteService _sqliteService;
        private readonly RotatorViewModel _rotatorViewModel;

        public List<int> RotatorValues { get; } = Enumerable.Range(0, 181).ToList();   // 0–180
        public List<int> AdjustmentValues { get; } = Enumerable.Range(1, 10).ToList(); // 1–10

        public RotatorOptionsViewModel(
            IMessenger messenger,
            ILogger<RotatorOptionsViewModel> logger,
            ApplicationStateService applicationStateService,
            SQLiteService sqliteService,
            RotatorViewModel rotatorViewModel)
        {
            Title = "Rotator Options";
            _messenger = messenger;
            _logger = logger;
            _applicationStateService = applicationStateService;
            _sqliteService = sqliteService;

            MinRotatorValue = Math.Clamp(Rotator.MinRotatorValue, 0, 180);
            MaxRotatorValue = Math.Clamp(Rotator.MaxRotatorValue, 0, 180);
            AdjustValue = Math.Clamp(Rotator.AdjustValue, 1, 10);

            UpdateStatus();
            _rotatorViewModel = rotatorViewModel;
        }

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(CanSave))]
        private int minRotatorValue;

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(CanSave))]
        private int maxRotatorValue;

        [ObservableProperty]
        private int adjustValue;

        [ObservableProperty]
        private string statusText;

        public bool IsMinMaxInvalid => MinRotatorValue >= MaxRotatorValue;
        public bool CanSave => !IsMinMaxInvalid;

        // Called automatically by CommunityToolkit whenever MinRotatorValue changes
        partial void OnMinRotatorValueChanged(int value) => UpdateStatus();

        // Called automatically by CommunityToolkit whenever MaxRotatorValue changes
        partial void OnMaxRotatorValueChanged(int value) => UpdateStatus();

        private void UpdateStatus()
        {
            StatusText = IsMinMaxInvalid
                ? "Invalid: Minimum value must be less than Maximum value"
                : "Ready";
        }

        [RelayCommand(CanExecute = nameof(CanSave))]
        private async Task Save()
        {
            IsBusy = true;
            try
            {
                Rotator.MinRotatorValue = MinRotatorValue;
                Rotator.MaxRotatorValue = MaxRotatorValue;
                Rotator.AdjustValue = AdjustValue;
                _sqliteService.config.MinRotatorValue = minRotatorValue;
                _sqliteService.config.MaxRotatorValue = maxRotatorValue;
                _sqliteService.config.AdjustValue = AdjustValue;

                if (await _sqliteService.SaveConfigAsync(true) == 1)
                {
                    StatusText = "Settings saved";
                }
                else
                {
                    StatusText = "Error saving settings";
                }


            }
            finally
            {
                IsBusy = false;
            }
        }

        [RelayCommand]
        private async Task GoBack()
        {
            await Shell.Current.GoToAsync("..");
        }


        [RelayCommand]
        private async Task MoveRotatorForward()
        {
            try
            {
                IsBusy = true;
                string result = await _rotatorViewModel.MoveRotatorForward();
                if (String.IsNullOrEmpty(result))
                {
                    StatusText = "Rotator failed to respond to move forward command";
                    return;
                }
                StatusText = "Rotator moving forward";
            }
            finally
            { IsBusy = false; }

        }

        [RelayCommand]
        private async Task MoveRotatorBackward()
        {
            try
            {
                IsBusy = true;
                string result = await _rotatorViewModel.MoveRotatorBackward();
                if (String.IsNullOrEmpty(result))
                {
                    StatusText = "Rotator failed to respond to move backward command";
                    return;
                }
                StatusText = "Rotator moving backward";
            }
            finally
            { IsBusy = false; }

        }

        [RelayCommand]
        private async Task StopRotator()
        {
            try
            {
                IsBusy = true;
                string result = await _rotatorViewModel.StopRotator();
                if (String.IsNullOrEmpty(result))
                {
                    StatusText = "Rotator failed to respond to move stop command";
                    return;
                }
                StatusText = "Rotator stopped";
            }
            finally
            { IsBusy = false; }
            

        }

        [RelayCommand]
        private async Task GetRotatorLocation()
        {
            try
            {
                IsBusy = true;
                string result = await _rotatorViewModel.GetRotatorLocation();
                if (String.IsNullOrEmpty(result))
                {
                    StatusText = "Rotator failed to respond to location request";
                    return;
                }
                StatusText = $"Current rotator location is {Rotator.ReturnCommandResponseAsDegrees(result)} degrees";
            }
            finally
            { IsBusy = false; }

        }


        [RelayCommand]
        private async Task RotatorPositionReset()
        {
            try
            {
                IsBusy = true;
                string result = await _rotatorViewModel.RotatorPositionReset();
                if (String.IsNullOrEmpty(result))
                {
                    StatusText = "Rotator failed to respond to set current location to zero command";
                    return;
                }
                StatusText = "Current rotator location set to zero degrees";
            }
            finally
            { IsBusy = false; }

        }

    }
}