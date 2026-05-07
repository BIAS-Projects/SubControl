using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using Microsoft.Extensions.Logging;

namespace SubControlMAUI.ViewModels;

public partial class RotorViewModel : BaseViewModel
{
    public RotorViewModel(IMessenger messenger,
        ILogger<PeriscopeViewModel> logger) : base(messenger, logger)
    {
        Title = "Rotor Control";

        ArmAngle = 73;
    }

    // =====================================================
    // ANGLE
    // =====================================================

    [ObservableProperty]
    private double armAngle;

    partial void OnArmAngleChanged(double value)
    {
        value = Math.Clamp(value, 0, 180);

        // TODO:
        // Send command to hardware here

        // Example:
        //
        // Send($"ROTOR ANGLE {(int)value}");
    }

    // =====================================================
    // COMMANDS
    // =====================================================

    [RelayCommand]
    private void Left()
    {
        ArmAngle = Math.Max(0, ArmAngle - 5);

        // Example:
        // Send("ROTOR LEFT");
    }

    [RelayCommand]
    private void Right()
    {
        ArmAngle = Math.Min(180, ArmAngle + 5);

        // Example:
        // Send("ROTOR RIGHT");
    }

    [RelayCommand]
    private void Stop()
    {
        // Example:
        // Send("ROTOR STOP");
    }
}