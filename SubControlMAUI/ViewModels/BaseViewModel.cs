using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Messaging;
using Microsoft.Extensions.Logging;
using SubControlMAUI.Messages;

namespace SubControlMAUI.ViewModels;

public partial class BaseViewModel : ObservableObject
{
    protected readonly IMessenger Messenger;
    protected readonly ILogger Logger;

    protected BaseViewModel(
        IMessenger messenger,
        ILogger logger)
    {
        Messenger = messenger;
        Logger = logger;

        RegisterMessengerEvents();
    }

    [ObservableProperty]
    string title = string.Empty;
    // =====================================================
    // COMMON UI STATE
    // =====================================================

    [ObservableProperty]
    private string status = "Disconnected";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsNotConnected))]
    private bool isConnected;

    public bool IsNotConnected => !IsConnected;

    [ObservableProperty]
    private bool isBusy;

    // =====================================================
    // MESSENGER REGISTRATION
    // =====================================================

    private void RegisterMessengerEvents()
    {
        Messenger.Register<TcpDataReceivedMessage>(
            this,
            async (r, msg) =>
            {
                await MainThread.InvokeOnMainThreadAsync(async () =>
                {
                    if (!await HandleTcpReceivedMessage(msg.Value.Command))
                    {
                        Status =
                            $"Error processing command: {msg.Value.Command}";
                    }
                    else
                    {
                        Status =
                            $"Success processing command: {msg.Value.Command}";
                    }
                });
            });

        Messenger.Register<TcpSendRequestMessage>(
            this,
            (r, msg) =>
            {
                MainThread.BeginInvokeOnMainThread(() =>
                {
                    Status = msg.Value.Command;
                });
            });

        Messenger.Register<TcpStatusMessage>(
            this,
            (r, msg) =>
            {
                MainThread.BeginInvokeOnMainThread(() =>
                {
                    Status = msg.Value;
                });
            });

        Messenger.Register<TcpErrorMessage>(
            this,
            (r, msg) =>
            {
                Logger?.LogError(
                    "TcpErrorMessage : {Error}",
                    msg.Value.Message);

                MainThread.BeginInvokeOnMainThread(() =>
                {
                    Status = msg.Value.Message;
                });
            });

        Messenger.Register<TcpAckTimeoutMessage>(
            this,
            (r, msg) =>
            {
                MainThread.BeginInvokeOnMainThread(() =>
                {
                    Status = $"No response to: {msg.Command}";
                });
            });

        Messenger.Register<TcpNackMessage>(
            this,
            (r, msg) =>
            {
                MainThread.BeginInvokeOnMainThread(() =>
                {
                    Status =
                        $"Server rejected '{msg.Command}': {msg.Reason}";
                });
            });

        Messenger.Register<TcpIsConnected>(
            this,
            (r, msg) =>
            {
                MainThread.BeginInvokeOnMainThread(() =>
                {
                    IsConnected = msg.Value;
                });
            });
    }

    // =====================================================
    // OVERRIDABLE MESSAGE HANDLER
    // =====================================================

    protected virtual Task<bool> HandleTcpReceivedMessage(string message)
    {
        return Task.FromResult(true);
    }
}