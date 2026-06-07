using CommunityToolkit.Mvvm.ComponentModel;
using SubControlMAUI.ViewModels;

namespace SubControlMAUI.Models
{
    public partial class Feature : ObservableObject
    {
        public enum Status
        {
            Unknown,
            CommOpen,
            CommClosed,
            Enabled,
            Disabled
        }


        public static string VideoName = "Video";
        public static string RotatorName = "Rotator";
        public static string TOMInput = "TOM Input";
        public static string TOMOutput = "TOM Output";
        public static string TOMAHRS = "TOM AHRS";
        public static string TOMGNSS = "TOM GNSS";
        public static string TOMFLIR = "TOM FLIR";
        public static string PushNotification = "PUSH";

        [ObservableProperty]
        private string name = string.Empty;

        [ObservableProperty]
        private bool isFitted = false;

        [ObservableProperty]
        private bool isCommPortOpen = false;

        [ObservableProperty]
        private bool isEnabled = false;

        // Derived status
        public Status CurrentStatus
        {
            get
            {
                if (!IsFitted)
                    return Status.Unknown;

                if (!IsCommPortOpen)
                    return Status.CommClosed;

                if (IsEnabled)
                    return Status.Enabled;

                return Status.Disabled;
            }
        }

        // Notify UI when dependent properties change
        partial void OnIsFittedChanged(bool value)
        {
            OnPropertyChanged(nameof(CurrentStatus));
        }

        partial void OnIsCommPortOpenChanged(bool value)
        {
            OnPropertyChanged(nameof(CurrentStatus));
        }

        partial void OnIsEnabledChanged(bool value)
        {
            OnPropertyChanged(nameof(CurrentStatus));
        }
    }
}