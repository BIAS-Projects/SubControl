using CommunityToolkit.Mvvm.ComponentModel;
using SubControlMAUI.Models;
using System;
using System.Collections.Generic;
using System.Text;

namespace SubControlMAUI.Services
{
    public partial class ApplicationStateService : ObservableObject
    {

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(IsNotConnected))]
        public bool isConnected = false;

        public bool IsNotConnected => !IsConnected;

        [ObservableProperty]
        public string connectionStatus = "Disconnected";

        [ObservableProperty]
        public bool allFeaturesEnabled = false;

        public List<Feature> Features = new List<Feature>()
        {
            new Feature {Name = Feature.RotatorName, IsFitted= false, IsCommPortOpen = false, IsEnabled = false },
            new Feature {Name = Feature.TOMInput, IsFitted= false, IsCommPortOpen = false, IsEnabled = false },
            new Feature {Name = Feature.TOMOutput, IsFitted= false, IsCommPortOpen = false, IsEnabled = false },
            new Feature {Name = Feature.TOMAHRS, IsFitted= false, IsCommPortOpen = false, IsEnabled = false },
            new Feature {Name = Feature.TOMGNSS, IsFitted= false, IsCommPortOpen = false, IsEnabled = false },
            new Feature {Name = Feature.TOMFLIR, IsFitted= false, IsCommPortOpen = false, IsEnabled = false },

    };


        public Feature GetFeatureByName(string name)
        {
            foreach (Feature feature in Features)
            {
                if (feature.Name == name) return feature;
            }
            return null;
        }

        public bool UpdateFeature (Feature feature)
        {
            for(int i = 0; i < Features.Count; i++)
            {
                if (feature.Name.Equals(Features[i].Name))
                {
                    Features[i] = feature;
                    return true;
                }
            }
            return false;
        }

        public bool IsVideoCommPortOpen =>
    GetFeatureByName(Feature.TOMInput)?.IsCommPortOpen == true &&
    GetFeatureByName(Feature.TOMOutput)?.IsCommPortOpen == true;

        public bool IsVideoEnabled =>
            GetFeatureByName(Feature.TOMInput)?.IsEnabled == true;


        public bool IsRotatorEnabled =>
    GetFeatureByName(Feature.RotatorName)?.IsEnabled == true;

        // Helper to set comm port state for both TOM ports atomically
        public void SetVideoCommPortOpen(bool isOpen)
        {
            var tomInput = GetFeatureByName(Feature.TOMInput);
            var tomOutput = GetFeatureByName(Feature.TOMOutput);

            if (tomInput is not null)
            {
                tomInput.IsCommPortOpen = isOpen;
                if (!isOpen) tomInput.IsEnabled = false;
                UpdateFeature(tomInput);
            }

            if (tomOutput is not null)
            {
                tomOutput.IsCommPortOpen = isOpen;
                if (!isOpen) tomOutput.IsEnabled = false;
                UpdateFeature(tomOutput);
            }
        }

        public void SetVideoEnabled(bool isEnabled)
        {
            var tomInput = GetFeatureByName(Feature.TOMInput);
            if (tomInput is not null)
            {
                tomInput.IsEnabled = isEnabled;
                UpdateFeature(tomInput);
            }
            OnPropertyChanged(nameof(IsVideoEnabled));  // ← notify bindings
        }

        public void SetRotatorEnabled(bool isEnabled)
        {
            var rotator = GetFeatureByName(Feature.RotatorName);
            if (rotator is not null)
            {
                rotator.IsEnabled = isEnabled;
                UpdateFeature(rotator);
            }
            OnPropertyChanged(nameof(IsRotatorEnabled));  // ← notify bindings
        }

    }
}
