using CommunityToolkit.Mvvm.Messaging;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Text;

namespace SubControlMAUI.ViewModels
{
    public partial class TechViewModel : BaseViewModel
    {
        public TechViewModel(IMessenger messenger,
        ILogger<PeriscopeViewModel> logger) : base(messenger, logger)
        {

            Title = "Technical";
        }

    }
}
