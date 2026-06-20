
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
        private readonly IAlertService _alertService;

        public List<int> RotatorValues { get; } = Enumerable.Range(0, 181).ToList();   // 0–180
        public List<int> AdjustmentValues { get; } = Enumerable.Range(1, 10).ToList(); // 1–10

        public List<int> DriveCurrentValues { get; } = Enumerable.Range(1, 100).ToList(); // 1–100

        public List<int> BrakePowerValues { get; } = Enumerable.Range(1, 100).ToList(); // 1–100

        public List<int> RotatorSpeeds { get; } = Enumerable.Range(1, 40).ToList(); // 1–40

        public List<int> MovementLimits { get; } =  Enumerable.Range(-360, 721).ToList(); // -360 - 360

        public List<int> AvailableRotatorSteps { get; } = Enumerable.Range(0, 4).ToList(); // 0-3

        public List<int> EepromAddresses { get; } = Enumerable.Range(0, 256).ToList(); // 0-255


        public RotatorOptionsViewModel(
            IMessenger messenger,
            ILogger<RotatorOptionsViewModel> logger,
            ApplicationStateService applicationStateService,
            SQLiteService sqliteService,
            RotatorViewModel rotatorViewModel,
            IAlertService alertService)
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
            _alertService = alertService;
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
        private int driveCurrentValue = 50;

        [ObservableProperty]
        private int rotatorSpeed = 20;

        [ObservableProperty]
        private int forwardLimit = 180;

        [ObservableProperty]
        private int backwardLimit = 0;

        [ObservableProperty]
        private int brakePower = 50;

        [ObservableProperty]
        private string eepromData = "";

        [ObservableProperty]
        private int rotatorStep = 1;

        [ObservableProperty]
        private int eepromReadAddress = 11;


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


        [RelayCommand]
        private async Task SetRotatorDriveCurrent()
        {
            try
            {
                IsBusy = true;
                string result = await _rotatorViewModel.SetMotorDriveCurrent(DriveCurrentValue);
                if (String.IsNullOrEmpty(result))
                {
                    StatusText = "Rotator failed to respond to set drive current command";
                    return;
                }
                StatusText = $"Rotator drive current set to {DriveCurrentValue} %";
            }
            finally
            { IsBusy = false; }

        }

        [RelayCommand]
        private async Task SetRotatorSpeed()
        {
            try
            {
                IsBusy = true;
                string result = await _rotatorViewModel.SetMotorSpeed(RotatorSpeed);
                if (String.IsNullOrEmpty(result))
                {
                    StatusText = "Rotator failed to respond to set drive current command";
                    return;
                }
                StatusText = $"Rotator drive current set to {RotatorSpeed}";
            }
            finally
            { IsBusy = false; }

        }

        [RelayCommand]
        private async Task SetRotatorForwardLimit()
        {
            try
            {
                IsBusy = true;
                string result = await _rotatorViewModel.SetMotorLimit(false, ForwardLimit);
                if (String.IsNullOrEmpty(result))
                {
                    StatusText = "Rotator failed to respond to set forward limit command";
                    return;
                }
                StatusText = $"Rotator forward limit set to {ForwardLimit}";
            }
            finally
            { IsBusy = false; }

        }

        [RelayCommand]
        private async Task SetRotatorBackwardLimit()
        {
            try
            {
                IsBusy = true;
                string result = await _rotatorViewModel.SetMotorLimit(false, BackwardLimit);
                if (String.IsNullOrEmpty(result))
                {
                    StatusText = "Rotator failed to respond to set backward limit command";
                    return;
                }
                StatusText = $"Rotator backward limit set to {BackwardLimit}";
            }
            finally
            { IsBusy = false; }

        }

        [RelayCommand]
        private async Task TurnOnBrake()
        {
            try
            {
                IsBusy = true;
                string result = await _rotatorViewModel.SetMotorBrake(true);
                if (String.IsNullOrEmpty(result))
                {
                    StatusText = "Rotator failed to respond to set turn on brake command";
                    return;
                }
                StatusText = $"Rotator brake turned on";
            }
            finally
            { IsBusy = false; }

        }

        [RelayCommand]
        private async Task SetBrakePower()
        {
            try
            {
                IsBusy = true;
                string result = await _rotatorViewModel.SetMotorBrakePower(BrakePower);
                if (String.IsNullOrEmpty(result))
                {
                    StatusText = "Rotator failed to respond to brake power command";
                    return;
                }
                StatusText = $"Rotator brake power set to {BrakePower}";
            }
            finally
            { IsBusy = false; }

        }

        [RelayCommand]
        private async Task TurnOffBrake()
        {
            try
            {
                IsBusy = true;
                string result = await _rotatorViewModel.SetMotorBrake(false);
                if (String.IsNullOrEmpty(result))
                {
                    StatusText = "Rotator failed to respond to set turn off brake command";
                    return;
                }
                StatusText = $"Rotator brake turned off";
            }
            finally
            { IsBusy = false; }

        }

        [RelayCommand]
        private async Task WriteEepromRegister()
        {
            try
            {
                IsBusy = true;
                string result = await _rotatorViewModel.WriteEepromRegister(EepromData);
                if (String.IsNullOrEmpty(result))
                {
                    StatusText = "Rotator failed to respond to write EEPROM data command";
                    return;
                }
                StatusText = $"{EepromData} written to EEPROM address last read from (MRE)";
            }
            finally
            { IsBusy = false; }

        }

        [RelayCommand]
        private async Task SetRotatorStepType()
        {
            try
            {
                IsBusy = true;
                string result = await _rotatorViewModel.SetMotorStepType(RotatorStep);
                if (String.IsNullOrEmpty(result))
                {
                    StatusText = "Rotator failed to respond to step motor step type command";
                    return;
                }
                StatusText = $"Rotator step type set to {RotatorStep}";
            }
            finally
            { IsBusy = false; }

        }

        [RelayCommand]
        private async Task RestoreFactoryDefaults()
        {
            try
            {
                IsBusy = true;
                string result = await _rotatorViewModel.RestoreFactoryDefaults();
                if (String.IsNullOrEmpty(result))
                {
                    StatusText = "Rotator failed to respond to restore factory defaults command";
                    return;
                }
                StatusText = $"Rotator factory defaults restored";
            }
            finally
            { IsBusy = false; }

        }

        [RelayCommand]
        private async Task SetCurrentRotatorPositionToZero()
        {
            try
            {
                IsBusy = true;
                string result = await _rotatorViewModel.RotatorPositionReset();
                if (String.IsNullOrEmpty(result))
                {
                    StatusText = "Rotator failed to respond to rotator position reset command";
                    return;
                }
                StatusText = $"Current rotator position set to zero degrees";
            }
            finally
            { IsBusy = false; }

        }

        [RelayCommand]
        private async Task GetDataFromEepromLocation()
        {
            try
            {
                IsBusy = true;
                string result = await _rotatorViewModel.ReadEepromLocation(eepromReadAddress);
                if (String.IsNullOrEmpty(result))
                {
                    StatusText = "Rotator failed to respond to read EEPROM address command";
                    return;
                }

                StatusText = $"EEPROM address: {EepromReadAddress} Contains {result.Substring(5, 4)}";
            }
            finally
            { IsBusy = false; }

        }


        [RelayCommand]
        private async Task GetRotatorSettings()
        {
            try
            {
                IsBusy = true;

                string output = "";

                string result = await _rotatorViewModel.GetSpeedSetting();
                if (String.IsNullOrEmpty(result))
                {
                    StatusText = "Rotator failed to respond to get speed settings command";
                    return;
                }

                output += $"Rotator speed: {result.Substring(5, 4).TrimStart('0')} (1 to 40) \r\n";

                result = await _rotatorViewModel.GetBackwardsLimit();
                if (String.IsNullOrEmpty(result))
                {
                    StatusText = "Rotator failed to respond to get backwards command command";
                    return;
                }

                output += $"Rotator backwards limit: {Rotator.ReturnCommandResponseAsDegrees(result)} degrees \r\n";

                result = await _rotatorViewModel.GetForwardsLimit();
                if (String.IsNullOrEmpty(result))
                {
                    StatusText = "Rotator failed to respond to get forward command command";
                    return;
                }

                output += $"Rotator forward limit: {Rotator.ReturnCommandResponseAsDegrees(result)} degrees \r\n";

                result = await _rotatorViewModel.GetBrakeSetting();
                if (String.IsNullOrEmpty(result))
                {
                    StatusText = "Rotator failed to respond to get brake settings command command";
                    return;
                }

                output += $"Rotator brake set to : {GetBrakeSettingFromCommand(result)} \r\n";

                result = await _rotatorViewModel.GetFirmwareVersion();
                if (String.IsNullOrEmpty(result))
                {
                    StatusText = "Rotator failed to respond to get firmware command";
                    return;
                }

                output += $"Rotator firmware version: {result.Substring(5, 4)}  \r\n";

                result = await _rotatorViewModel.GetBrakePower();
                if (String.IsNullOrEmpty(result))
                {
                    StatusText = "Rotator failed to respond to get brake power command";
                    return;
                }

                output += $"Rotator brake power: {result.Substring(5, 4).TrimStart('0')} %  \r\n";


                result = await _rotatorViewModel.GetMotorDriveCurrent();
                if (String.IsNullOrEmpty(result))
                {
                    StatusText = "Rotator failed to respond to get motor drive current command";
                    return;
                }

                output += $"Rotator motor drive current: {result.Substring(5, 4).TrimStart('0')} % \r\n";

                result = await _rotatorViewModel.GetMotorStepType();
                if (String.IsNullOrEmpty(result))
                {
                    StatusText = "Rotator failed to respond to get motor step type command";
                    return;
                }

                output += $"Rotator motor step type: {GetMotorStepType(result)}  \r\n";

                result = await _rotatorViewModel.GetMotorTemp();
                if (String.IsNullOrEmpty(result))
                {
                    StatusText = "Rotator failed to respond to get motor temperature command";
                    return;
                }

                output += $"Rotator motor temperature : {result.Substring(5, 4).TrimStart('0')} degrees Celsius \r\n";

                await _alertService.ShowAlertAsync(
                    "Information",
                    $"{output}",
                    "OK");


            }
            finally
            { IsBusy = false; }

        }

        private static string GetMotorStepType(string response)
        {
            if (string.IsNullOrWhiteSpace(response))
                throw new ArgumentException("Response cannot be empty.");

            // Example response: "#AMRM0003R"
            if (response.Length < 10)
                throw new FormatException("Invalid response format.");

            string code = response.Substring(5, 4);

            return code switch
            {
                "0000" => "Wave",
                "0001" => "Full",
                "0002" => "Half",
                "0003" => "Sine",
                _ => $"Unknown ({code})"
            };
        }

        private static string GetBrakeSettingFromCommand(string response)
        {
            string code = response.Substring(5, 4);
            return code switch
            {
                "0000" => "Off",
                "0001" => "On",
                _ => $"Unknown ({code})"
            };
        }

    }
}