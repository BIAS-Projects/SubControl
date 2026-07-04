using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using Microsoft.Extensions.Logging;
using SubControlMAUI.Pages;
using SubControlMAUI.Services;
using System;
using System.Collections.Generic;
using System.Text;

namespace SubControlMAUI.ViewModels
{
    public partial class TechViewModel : BaseViewModel
    {
        IMessenger _messenger;
        ILogger<TechViewModel> _logger;
        INavigationService _navigation;

        public TechViewModel(IMessenger messenger,
        ILogger<TechViewModel> logger,
        INavigationService navigation)
        {
            _logger = logger;
            _messenger = messenger;
            _navigation = navigation;
            Title = "Technical";
        }


        // COMMANDS
        [RelayCommand]
        private async Task Pi()
        {
            await _navigation.GoToAsync(nameof(PiPage));
        }

        [RelayCommand]
        private async Task Console()
        {
            await _navigation.GoToAsync(nameof(ConsolePage));
        }

        [RelayCommand]
        private async Task Logs()
        {
            await _navigation.GoToAsync(nameof(LogsPage));
        }



        [RelayCommand]
        private async Task GoBack()
        {
            await Shell.Current.GoToAsync("..");
        }

    }
}
