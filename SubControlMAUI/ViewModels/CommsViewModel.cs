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



            //WeakReferenceMessenger.Default.Register<TCPReceiveMessage>(this, (r, m) =>
            //{
            //    MainThread.BeginInvokeOnMainThread(() =>
            //    {
            //        UpdateText(m.Value);
            //    });

            //});

            _messenger.Register<TcpDataReceivedMessage>(this, (r, m) =>
            {
                MainThread.BeginInvokeOnMainThread(() =>
                {
                    UpdateText(Encoding.UTF8.GetString(m.Value));
                });

            });

            _messenger.Register<TcpSendRequestMessage>(this, (r, m) =>
            {
                MainThread.BeginInvokeOnMainThread(() =>
                {
                    UpdateText(Encoding.UTF8.GetString(m.Value));
                });

            });

            _messenger.Register<TcpStatusMessage>(this, (r, m) =>
            {
                MainThread.BeginInvokeOnMainThread(() =>
                {
                    UpdateText(m.Value);
                });

            });

            _messenger.Register<TcpErrorMessage>(this, (r, m) =>
            {
                MainThread.BeginInvokeOnMainThread(() =>
                {
                    UpdateText(m.Value.Message);
                });

            });


        }

        [ObservableProperty]
        string receivedText;

        public void UpdateText(string text)
        {
            ReceivedText += text + Environment.NewLine;
        }

    } 
}
