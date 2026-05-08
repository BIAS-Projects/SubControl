using CommunityToolkit.Mvvm.ComponentModel;
using System;
using System.Collections.Generic;
using System.Text;

namespace SubControlMAUI.ViewModels
{
    public partial class BaseViewModel : ObservableObject
    {
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(IsNotBusy))]
        bool isBusy;

        [ObservableProperty]
        string title = string.Empty;

        public bool IsNotBusy => !IsBusy;

    }
}
