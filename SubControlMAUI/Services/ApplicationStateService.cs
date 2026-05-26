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
    }
}
