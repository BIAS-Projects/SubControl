using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using CommunityToolkit.Mvvm.Messaging.Messages;
using SubControlMAUI.Messages;
using System;
using System.Collections.Generic;
using System.Text;

namespace SubControlMAUI.ViewModels
{
    public partial class CommsViewModel : BaseViewModel
    {
        private readonly IMessenger _messenger;
        public CommsViewModel(IMessenger messenger)
        {
            Title = "Comms";
            _messenger = messenger;





        }



    } 
}
