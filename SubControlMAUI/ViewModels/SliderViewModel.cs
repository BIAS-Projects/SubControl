namespace SubControlMAUI.ViewModels;


using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using Microsoft.Extensions.Logging;
using SubConsole.Models;
using SubControlMAUI.Messages;
using SubControlMAUI.Models;
using SubControlMAUI.Services;
using System.Diagnostics;
using Windows.UI.ApplicationSettings;




// ─────────────────────────────────────────────────────────────────────────────
// SliderViewModel
// ─────────────────────────────────────────────────────────────────────────────
public partial class SliderViewModel : BaseViewModel
{
    // ── gauge ─────────────────────────────────────────────────────────────────
    public RotatorGaugeDrawable GaugeDrawable { get; } = new();
    public Action? RefreshGaugeView { get; set; }

    private static string ThemePrefix =>
    Application.Current?.RequestedTheme == AppTheme.Dark ? "dark" : "light";





    // ── services ──────────────────────────────────────────────────────────────
    private readonly ILogger<SliderViewModel> _logger;
    private readonly IMessenger _messenger;
    private readonly TcpSocketService _tcp;
    private readonly IAlertService _alertService;

    private int interCommandDelay = 500;

    private readonly TimeSpan _commandTimeout = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Shared dispatcher for request/response and push-confirm operations.
    /// The rotator exclusively uses push-confirm (SendAndWaitForPushAsync) because
    /// the device never echoes a CommandResponse JSON body.
    /// </summary>
    private readonly CommandDispatcherService _dispatcher;

    public ApplicationStateService AppState { get; }

    // ── command serialisation ─────────────────────────────────────────────────
    private readonly SemaphoreSlim _commandLock = new(1, 1);





    // ─────────────────────────────────────────────────────────────────────────
    // Constructor
    // ─────────────────────────────────────────────────────────────────────────
    public SliderViewModel(
        IMessenger messenger,
        ILogger<SliderViewModel> logger,
        TcpSocketService tcp,
        ApplicationStateService appState,
        IAlertService alertService,
        CommandDispatcherService dispatcher)
    {
        Title = "Slider Control";
        _logger = logger;
        _messenger = messenger;
        AppState = appState;
        _tcp = tcp;
        _alertService = alertService;
        _dispatcher = dispatcher;
        _dispatcher.Owner = nameof(SliderViewModel);


         RegisterMessages();
    }

     //─────────────────────────────────────────────────────────────────────────
     //Message registration
     //─────────────────────────────────────────────────────────────────────────
    private void RegisterMessages()
    {
        _messenger.Register<TcpDataReceivedMessage>(this, (_, msg) =>
        {
            // Let the dispatcher resolve any pending push-confirm first
            _dispatcher.HandleIncoming(
                msg.Value.Function,
                msg.Value.Command,
                msg.Value.Data);

            // Then do SliderViewModel-specific processing
            _ = HandleTcpDataAsync(msg);
        });

        _messenger.Register<TcpStatusMessage>(this, (_, msg) =>
            SetStatus(msg.Value));

        _messenger.Register<TcpErrorMessage>(this, (_, msg) =>
        {
            _logger.LogError("TcpErrorMessage: {Message}", msg.Value.Message);
            SetStatus(msg.Value.Message);
        });

        _messenger.Register<TcpAckTimeoutMessage>(this, (_, msg) =>
            SetStatus($"No response to: {msg.Command}"));

        _messenger.Register<TcpNackMessage>(this, (_, msg) =>
            SetStatus($"Server rejected '{msg.Command}': {msg.Reason}"));

        _messenger.Register<TcpIsConnected>(this, (_, msg) =>
        {
            if (!msg.Value)
                MainThread.BeginInvokeOnMainThread(async () =>
                    await Shell.Current.GoToAsync("//MainPage"));
        });
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Bindings
    // ─────────────────────────────────────────────────────────────────────────

    [ObservableProperty] private string statusText = string.Empty;

    [ObservableProperty] private string message = string.Empty;

    [ObservableProperty] private string sliderReceivedMessage = string.Empty;

    // ─────────────────────────────────────────────────────────────────────────
    // Helpers
    // ─────────────────────────────────────────────────────────────────────────

    private void SetStatus(string text)
    {
        if (MainThread.IsMainThread) StatusText = text;
        else MainThread.BeginInvokeOnMainThread(() => StatusText = text);
    }



    // ─────────────────────────────────────────────────────────────────────────
    // TCP data handler

    // The dispatcher already resolved any pending push-confirm before this
    // method is called.  Here we only process the received data; we don't need to touch the dispatcher again.
    // ─────────────────────────────────────────────────────────────────────────
    private Task HandleTcpDataAsync(TcpDataReceivedMessage msg)
    {
        try
        {
            if(!msg.Value.Function.Equals(Feature.SliderName))
            {
                return Task.CompletedTask;
            }
            if(!msg.Value.Data.Contains(Slider.MessageHeader))
            {
                return Task.CompletedTask;
            }
            Message = msg.Value.Data;

        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "HandleTcpDataAsync threw unexpectedly");
        }

        return Task.CompletedTask;
    }

    public void OnAppearing()
    {

    }

    public void OnDisappearing()
    {

    }

    [RelayCommand]
    private async Task GoBack()
    {
        StatusText = string.Empty;
        await Shell.Current.GoToAsync("..");
    }

    [RelayCommand]
    private async Task SendSliderMessage()
    {
        SliderReceivedMessage = String.Empty;
        StatusText = String.Empty;
        var espResponse = await _dispatcher.SendAndWaitForPushAsync(
         Feature.SliderName, "WRITE TEXT",
         "T",
         Feature.SliderName,
         r => r.Contains("You sent"),
         _commandTimeout);

        if (espResponse is null)
        {
            SetStatus("ESP failed to respond to command");
            return;
        }
        SliderReceivedMessage = espResponse;
    }

}