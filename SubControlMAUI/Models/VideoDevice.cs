using CommunityToolkit.Mvvm.ComponentModel;
using System;
using System.Collections.Generic;
using System.Text;

namespace SubControlMAUI.Models
{
    public partial class VideoDevice : ObservableObject
    {
        public Action<VideoDevice, string> OnFunctionSelected { get; set; }

        [ObservableProperty]
        private string _name;

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
        { "Unselected", "Video", "FLIR" };
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
}
