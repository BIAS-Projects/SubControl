using CommunityToolkit.Mvvm.Messaging;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Text;

namespace SubControlMAUI.ViewModels
{
    public partial class TechViewModel : BaseViewModel
    {
        IMessenger _messenger;
        ILogger<TechViewModel> _logger;

        public TechViewModel(IMessenger messenger,
        ILogger<TechViewModel> logger)
        {
            _logger = logger;
            _messenger = messenger;
            Title = "Technical";
        }

    }
}
