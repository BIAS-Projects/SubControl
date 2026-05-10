using CommunityToolkit.Mvvm.ComponentModel;

namespace SubControlMAUI.Models;

public partial class UsbDevice : ObservableObject
{
    public Action<UsbDevice, string> OnFunctionSelected { get; set; }

    [ObservableProperty]
    private string _name;

    [ObservableProperty]
    private string _description = "";

    private string _selectedFunction = "Unselected";
    public string SelectedFunction
    {
        get => _selectedFunction;
        set
        {
            if (string.IsNullOrEmpty(value)) return;
            if (_selectedFunction == value) return;
            _selectedFunction = value;
            OnPropertyChanged();
            OnFunctionSelected?.Invoke(this, value);
        }
    }

    // Picker binds to this — replaced wholesale, no mutation
    private List<string> _availableFunctions = new()
        { "Unselected", "TOM Input", "TOM Output", "TOM AHRS", "TOM GNSS", "TOM FLIR", "ROTOR" };
    public List<string> AvailableFunctions
    {
        get => _availableFunctions;
        private set
        {
            _availableFunctions = value;
            OnPropertyChanged();
        }
    }

    public void SetSelectionSilently(string value)
    {
        _selectedFunction = string.IsNullOrEmpty(value) ? "Unselected" : value;
        OnPropertyChanged(nameof(SelectedFunction));
    }

    public void UpdateAvailableFunctions(List<string> available)
    {
        AvailableFunctions = new List<string>(available);
        // Force the Picker to re-evaluate SelectedItem against the new list
        OnPropertyChanged(nameof(SelectedFunction));
    }
}