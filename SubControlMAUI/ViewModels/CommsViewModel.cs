using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using CommunityToolkit.Mvvm.Messaging.Messages;
using Microsoft.Extensions.Logging;
using SubControlMAUI.Messages;
using System;
using System.Collections.Generic;
using System.Text;

namespace SubControlMAUI.ViewModels
{
    public partial class CommsViewModel : BaseViewModel
    {
        private readonly IMessenger _messenger;
        private readonly ILogger<CommsViewModel> _logger;
        public CommsViewModel(IMessenger messenger,
        ILogger<CommsViewModel> logger)
        {
            Title = "Comms";
            _messenger = messenger;
            _logger = logger;





        }



    } 
}
