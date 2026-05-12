using CommunityToolkit.Mvvm.ComponentModel;
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
    }
}
