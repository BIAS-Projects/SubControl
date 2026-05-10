using CommunityToolkit.Mvvm.ComponentModel;

namespace SubControlMAUI.Models;

public partial class CameraDevice : ObservableObject
{
    public Action<CameraDevice, string>? OnProfileSelected { get; set; }

    [ObservableProperty]
    private string _deviceId = "";

    [ObservableProperty]
    private string _friendlyName = "";

    [ObservableProperty]
    private bool _isRegisteredWithMtx;

    private string _selectedProfile = "Unselected";
    public string SelectedProfile
    {
        get => _selectedProfile;
        set
        {
            if (string.IsNullOrEmpty(value)) return;
            if (_selectedProfile == value) return;
            _selectedProfile = value;
            OnPropertyChanged();
            OnProfileSelected?.Invoke(this, value);
        }
    }

    private List<string> _availableProfiles = new() { "Unselected" };
    public List<string> AvailableProfiles
    {
        get => _availableProfiles;
        private set
        {
            _availableProfiles = value;
            OnPropertyChanged();
        }
    }

    public void SetSelectionSilently(string value)
    {
        _selectedProfile = string.IsNullOrEmpty(value) ? "Unselected" : value;
        OnPropertyChanged(nameof(SelectedProfile));
    }

    public void UpdateAvailableProfiles(List<string> available)
    {
        AvailableProfiles = new List<string>(available);
        // Force the Picker to re-evaluate SelectedItem against the new list
        OnPropertyChanged(nameof(SelectedProfile));
    }
}