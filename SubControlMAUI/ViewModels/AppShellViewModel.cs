using CommunityToolkit.Mvvm.ComponentModel;
using System;
using System.Collections.Generic;
using System.Text;

namespace SubControlMAUI.ViewModels
{
    public partial class AppShellViewModel : BaseViewModel
    {
        [ObservableProperty]
        private bool cutterEnabled = true;

        [ObservableProperty]
        private bool periscopeEnabled = true;

        public AppShellViewModel()
        {
            Title = "SubControlMAUI";

        }
    }
}
