namespace SubControlMAUI.ViewModels;

using CommunityToolkit.Maui.Extensions;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using Microsoft.Extensions.Logging;

using SubConsole.Models;
using SubControlMAUI.Messages;
using SubControlMAUI.Models;
using SubControlMAUI.Pages;
using SubControlMAUI.Popups;
using SubControlMAUI.Services;
using System.Text.Json;

public partial class MainViewModel : BaseViewModel
{
    // -------------------------------------------------------------------------
    // Services
    // -------------------------------------------------------------------------

    private readonly SQLiteService _sqliteService;
    private readonly IAlertService _alertService;
    private readonly INavigationService _navigationService;
    private readonly ILogger<MainViewModel> _loggerService;
    private readonly IMessenger _messengerService;
    private readonly TcpSocketService _tcpService;
    private readonly RotatorViewModel _rotatorViewModel;

    /// <summary>
    /// Handles all TCP request/response and push-confirm logic for this ViewModel.
    /// Each ViewModel gets its own instance (registered as Transient in DI) so
    /// their one-slot pending state never collides.
    /// </summary>
    private readonly CommandDispatcherService _dispatcher;

    public ApplicationStateService AppState { get; }

    // -------------------------------------------------------------------------
    // Protocol constants
    // -------------------------------------------------------------------------

    // Feature names used as the Function field in outgoing TCP messages.
    // "SerialFeature" routes to the server's serial-port manager (OPEN/CLOSE/LIST).
    private static string SerialFeature => nameof(MainViewModel);
    private static string CameraFeature => nameof(MainViewModel) + "CAMERA";
    private static string RotatorFeature => Feature.RotatorName;

    private readonly TimeSpan _timeout = TimeSpan.FromSeconds(10);

    // -------------------------------------------------------------------------
    // Observable properties
    // -------------------------------------------------------------------------

    [ObservableProperty]
    private bool isConnected;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsNotSystemEnabled))]
    [NotifyPropertyChangedFor(nameof(CanEnableSystem))]
    [NotifyPropertyChangedFor(nameof(CanDisableSystem))]
    [NotifyPropertyChangedFor(nameof(SystemToggleButtonImage))]
    private bool isSystemEnabled;

    public bool IsNotSystemEnabled => !IsSystemEnabled;
    public bool CanEnableSystem => AppState.IsConnected && !IsSystemEnabled;
    public bool CanDisableSystem => AppState.IsConnected && IsSystemEnabled;

    public bool IsVideoEnabled => AppState.IsVideoEnabled;
    public bool IsRotatorEnabled => AppState.IsRotatorEnabled;

    // Tracks whether the rotator was previously enabled so the toggle state
    // machine knows which direction to step through next.
    public bool RotatorPreviouslyEnabled { get; private set; }

    public bool CanEnableVideo =>
        AppState.IsConnected &&
        AppState.GetFeatureByName(Feature.TOMInput)?.IsFitted == true;

    public bool CanEnableRotator =>
        AppState.IsConnected &&
        AppState.GetFeatureByName(Feature.RotatorName)?.IsFitted == true;

    [ObservableProperty]
    private double buttonSize;

    [ObservableProperty]
    private double layoutSpacing;

    [ObservableProperty]
    private string statusText = "Disconnected";

    [ObservableProperty]
    private bool isDarkTheme;

    [ObservableProperty]
    private Color videoStatusColor;

    [ObservableProperty]
    private Color rotatorStatusColor;

    // -------------------------------------------------------------------------
    // Status colour map
    // -------------------------------------------------------------------------

    public enum Status { Unknown, CommOpen, CommClosed, Enabled, Disabled }

    private readonly Dictionary<Status, Color> _statusColours = new()
    {
        [Status.CommOpen] = Colors.Orange,
        [Status.CommClosed] = Colors.Red,
        [Status.Enabled] = Colors.Green,
        [Status.Disabled] = Colors.Orange,
        [Status.Unknown] = Colors.Gray,
    };

    // -------------------------------------------------------------------------
    // Button image helpers
    // -------------------------------------------------------------------------

    private static string ThemePrefix =>
        Application.Current?.RequestedTheme == AppTheme.Dark ? "dark" : "light";

    public string RotatorEnableButtonImage =>
        $"{ThemePrefix}_{(IsRotatorEnabled ? "on" : "off")}_button.png";

    public string VideoEnableButtonImage =>
        $"{ThemePrefix}_{(IsVideoEnabled ? "on" : "off")}_button.png";

    public string SystemToggleButtonImage =>
        $"{ThemePrefix}_{(IsSystemEnabled ? "on" : "off")}_button.png";

    // -------------------------------------------------------------------------
    // Constructor
    // -------------------------------------------------------------------------

    public MainViewModel(
        SQLiteService sqliteService,
        IAlertService alertService,
        IMessenger messengerService,
        CommandDispatcherService dispatcher,
        ILogger<MainViewModel> loggerService,
        INavigationService navigationService,
        ApplicationStateService applicationStateService,
             TcpSocketService tcpService,
             RotatorViewModel rotatorViewModel)
    {


        Title = "Main Menu";
        _sqliteService = sqliteService;
        _alertService = alertService;
        _messengerService = messengerService;
        _dispatcher = dispatcher;
        _dispatcher.Owner = nameof(MainViewModel);
        _loggerService = loggerService;
        _navigationService = navigationService;
        AppState = applicationStateService;
        _tcpService = tcpService;

        videoStatusColor = _statusColours[Status.Unknown];
        rotatorStatusColor = _statusColours[Status.Unknown];

        RegisterMessages();





        AppState.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(AppState.IsConnected))
            {
                OnPropertyChanged(nameof(CanEnableSystem));
                OnPropertyChanged(nameof(CanDisableSystem));
                OnPropertyChanged(nameof(CanEnableVideo));
                OnPropertyChanged(nameof(CanEnableRotator));
                IsConnected = AppState.IsConnected;
            }
            if (e.PropertyName == nameof(AppState.IsVideoEnabled))
            {
                OnPropertyChanged(nameof(IsVideoEnabled));
                OnPropertyChanged(nameof(CanEnableVideo));
            }
            if (e.PropertyName == nameof(AppState.IsRotatorEnabled))
            {
                OnPropertyChanged(nameof(IsRotatorEnabled));
            }
        };

        App.ThemeChanged += () =>
        {
            OnPropertyChanged(nameof(RotatorEnableButtonImage));
            OnPropertyChanged(nameof(VideoEnableButtonImage));
            OnPropertyChanged(nameof(SystemToggleButtonImage));
        };
        _rotatorViewModel = rotatorViewModel;
    }

    // -------------------------------------------------------------------------
    // Message registration
    // -------------------------------------------------------------------------

    private void RegisterMessages()
    {
        _messengerService.Register<TcpDataReceivedMessage>(this, async (_, msg) =>
        {
            // Route every incoming message through the dispatcher.
            // The dispatcher resolves whichever pending operation (if any) matches.
            _dispatcher.HandleIncoming(
                msg.Value.Function,
                msg.Value.Command,
                msg.Value.Data);
        });

        _messengerService.Register<TcpStatusMessage>(this, (_, msg) =>
            MainThread.BeginInvokeOnMainThread(() => StatusText = msg.Value));

        _messengerService.Register<TcpErrorMessage>(this, (_, msg) =>
        {
            _loggerService.LogError("TcpErrorMessage: {Message}", msg.Value.Message);
            MainThread.BeginInvokeOnMainThread(() => StatusText = msg.Value.Message);
        });

        _messengerService.Register<TcpAckTimeoutMessage>(this, (_, msg) =>
            MainThread.BeginInvokeOnMainThread(
                () => StatusText = $"No response to: {msg.Command}"));

        _messengerService.Register<TcpNackMessage>(this, (_, msg) =>
            MainThread.BeginInvokeOnMainThread(
                () => StatusText = $"Server rejected '{msg.Command}': {msg.Reason}"));

        _messengerService.Register<TcpIsConnected>(this, (_, msg) =>
        {
            MainThread.BeginInvokeOnMainThread(() => IsConnected = msg.Value);
        });

    }

    // -------------------------------------------------------------------------
    // Connect / Disconnect
    // -------------------------------------------------------------------------

    [RelayCommand]
    private async Task Connect()
    {
        IsBusy = true;
        try
        {
            await _tcpService.StartAsync(
                _sqliteService.config.IPAddress,
                int.Parse(_sqliteService.config.Port));

            if (!AppState.IsConnected)
            {
                StatusText = "Connection failed";
                return;
            }

            StatusText = "Querying device capabilities...";
            var serialFunctions = await QuerySerialRegisteredAsync();

            foreach (var feature in AppState.Features.ToList())
            {
                feature.IsFitted = serialFunctions.Contains(feature.Name);
                feature.IsCommPortOpen = false;
                feature.IsEnabled = false;
                AppState.UpdateFeature(feature);
            }

            var tomInput = AppState.GetFeatureByName(Feature.TOMInput);
            var rotator = AppState.GetFeatureByName(Feature.RotatorName);

            MainThread.BeginInvokeOnMainThread(() =>
            {
                VideoStatusColor = tomInput switch
                {
                    null or { IsFitted: false } => _statusColours[Status.Unknown],
                    { IsCommPortOpen: true } => _statusColours[Status.CommOpen],
                    _ => _statusColours[Status.CommClosed]
                };

                RotatorStatusColor = rotator switch
                {
                    null or { IsFitted: false } => _statusColours[Status.Unknown],
                    { IsCommPortOpen: true } => _statusColours[Status.CommOpen],
                    _ => _statusColours[Status.CommClosed]
                };

                OnPropertyChanged(nameof(CanEnableVideo));
                OnPropertyChanged(nameof(CanEnableRotator));
            });

            StatusText = "Connected";
        }
        catch (Exception ex)
        {
            StatusText = $"Error: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task Disconnect()
    {
        IsBusy = true;
        try
        {
            var tomInput = AppState.GetFeatureByName(Feature.TOMInput);
            if (tomInput?.IsFitted == true && tomInput.IsEnabled)
            {
                StatusText = "Disabling video...";
                await DisableVideo();           // best-effort; proceed regardless
                AppState.SetVideoEnabled(false);
            }

            var rotator = AppState.GetFeatureByName(Feature.RotatorName);
            if (rotator?.IsFitted == true && rotator.IsEnabled)
            {
                rotator.IsEnabled = false;
                AppState.UpdateFeature(rotator);
                AppState.SetRotatorEnabled(false);
                RotatorPreviouslyEnabled = false;
            }

            foreach (var feature in AppState.Features.ToList().Where(f => f.IsCommPortOpen))
            {
                StatusText = $"Closing {feature.Name}...";
                await CloseFeatureCommPort(feature.Name);
                feature.IsCommPortOpen = false;
                feature.IsEnabled = false;
                AppState.UpdateFeature(feature);
            }

            await _tcpService.StopAsync();

            foreach (var feature in AppState.Features.ToList())
            {
                feature.IsCommPortOpen = false;
                feature.IsEnabled = false;
                feature.IsFitted = false;
                AppState.UpdateFeature(feature);
            }

            IsSystemEnabled = false;
            RotatorPreviouslyEnabled = false;

            MainThread.BeginInvokeOnMainThread(() =>
            {
                VideoStatusColor = _statusColours[Status.Unknown];
                RotatorStatusColor = _statusColours[Status.Unknown];
                OnPropertyChanged(nameof(CanEnableVideo));
                OnPropertyChanged(nameof(CanEnableRotator));
            });

            StatusText = "Disconnected";
        }
        finally
        {
            IsBusy = false;
        }
    }

    // -------------------------------------------------------------------------
    // ToggleSystem
    // -------------------------------------------------------------------------

    [RelayCommand]
    private async Task ToggleSystem()
    {
        if (AppState.IsNotConnected) return;

        IsBusy = true;
        try
        {
            if (!IsSystemEnabled)
                await EnableSystemAsync();
            else
                await DisableSystemAsync();
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task EnableSystemAsync()
    {
        // 1. Camera prerequisites
        StatusText = "Checking FFmpeg...";
        if (await _dispatcher.SendAndWaitAsync(CameraFeature, "CHECK FFMPEG", "", _timeout) is null)
        {
            StatusText = "Enable failed — FFmpeg not available";
            return;
        }

        StatusText = "Checking MediaMTX...";
        if (await _dispatcher.SendAndWaitAsync(CameraFeature, "CHECK MTX VERSION", "", _timeout) is null)
        {
            StatusText = "Enable failed — MediaMTX not available";
            return;
        }

        // 2. Open comm ports for all fitted features
        foreach (var feature in AppState.Features.ToList().Where(f => f.IsFitted && !f.IsCommPortOpen))
        {
            StatusText = $"Opening {feature.Name}...";
            if (await OpenFeatureCommPort(feature.Name))
            {
                feature.IsCommPortOpen = true;
                AppState.UpdateFeature(feature);
            }
        }

        // 3. Enable video
        var tomInput = AppState.GetFeatureByName(Feature.TOMInput);
        if (tomInput?.IsFitted == true && tomInput.IsCommPortOpen && !tomInput.IsEnabled)
        {
            StatusText = "Enabling video...";
            if (await EnableVideo())
            {
                AppState.SetVideoEnabled(true);
                StatusText = "Video enabled";
            }
        }

        // 4. Enable rotator
        var rotator = AppState.GetFeatureByName(Feature.RotatorName);
        if (rotator?.IsFitted == true && rotator.IsCommPortOpen && !rotator.IsEnabled)
        {
            StatusText = "Enabling rotator...";
            rotator.IsEnabled = true;
            AppState.UpdateFeature(rotator);
            AppState.SetRotatorEnabled(true);
            RotatorPreviouslyEnabled = true;
            StatusText = "Rotator enabled";
        }

        // 5. Refresh colours
        tomInput = AppState.GetFeatureByName(Feature.TOMInput);
        rotator = AppState.GetFeatureByName(Feature.RotatorName);

        MainThread.BeginInvokeOnMainThread(() =>
        {
            VideoStatusColor = tomInput switch
            {
                { IsEnabled: true } => _statusColours[Status.Enabled],
                { IsCommPortOpen: true } => _statusColours[Status.Disabled],
                { IsFitted: true } => _statusColours[Status.CommClosed],
                _ => _statusColours[Status.Unknown]
            };

            RotatorStatusColor = rotator switch
            {
                { IsEnabled: true } => _statusColours[Status.Enabled],
                { IsCommPortOpen: true } => _statusColours[Status.Disabled],
                { IsFitted: true } => _statusColours[Status.CommClosed],
                _ => _statusColours[Status.Unknown]
            };

            OnPropertyChanged(nameof(CanEnableVideo));
            OnPropertyChanged(nameof(CanEnableRotator));
        });

        IsSystemEnabled = true;
        StatusText = "System enabled";
    }

    private async Task DisableSystemAsync()
    {
        var tomInput = AppState.GetFeatureByName(Feature.TOMInput);
        if (tomInput?.IsEnabled == true)
        {
            StatusText = "Disabling video...";
            await DisableVideo();               // best-effort
            AppState.SetVideoEnabled(false);
        }

        var rotator = AppState.GetFeatureByName(Feature.RotatorName);
        if (rotator?.IsEnabled == true)
        {
            rotator.IsEnabled = false;
            AppState.UpdateFeature(rotator);
            AppState.SetRotatorEnabled(false);
            RotatorPreviouslyEnabled = false;
        }

        foreach (var feature in AppState.Features.ToList().Where(f => f.IsCommPortOpen))
        {
            StatusText = $"Closing {feature.Name}...";
            if (await CloseFeatureCommPort(feature.Name))
            {
                feature.IsCommPortOpen = false;
                feature.IsEnabled = false;
                AppState.UpdateFeature(feature);
            }
        }

        tomInput = AppState.GetFeatureByName(Feature.TOMInput);
        rotator = AppState.GetFeatureByName(Feature.RotatorName);

        MainThread.BeginInvokeOnMainThread(() =>
        {
            VideoStatusColor = tomInput switch
            {
                null or { IsFitted: false } => _statusColours[Status.Unknown],
                _ => _statusColours[Status.CommClosed]
            };

            RotatorStatusColor = rotator switch
            {
                null or { IsFitted: false } => _statusColours[Status.Unknown],
                _ => _statusColours[Status.CommClosed]
            };

            OnPropertyChanged(nameof(CanEnableVideo));
            OnPropertyChanged(nameof(CanEnableRotator));
        });

        IsSystemEnabled = false;
        StatusText = "System disabled";
    }

    // -------------------------------------------------------------------------
    // ToggleVideoEnable
    //
    // State machine:
    //   Off (both ports closed)
    //     → Open both ports → Disabled
    //   Disabled (ports open, not enabled)
    //     → Send TOM on → Enabled
    //   Enabled
    //     → Send TOM off → close Output port → Disabled
    //   Disabled (Output closed, only Input open)
    //     → Close Input port → Off
    // -------------------------------------------------------------------------

    [RelayCommand]
    private async Task ToggleVideoEnable()
    {
        var tomInput = AppState.GetFeatureByName(Feature.TOMInput);
        var tomOutput = AppState.GetFeatureByName(Feature.TOMOutput);
        if (tomInput is null || !tomInput.IsFitted) return;

        IsBusy = true;
        try
        {
            if (!tomInput.IsCommPortOpen && !tomOutput.IsCommPortOpen)
            {
                // Off → Disabled
                StatusText = "Opening TOM Input...";
                if (!await OpenFeatureCommPort(Feature.TOMInput))
                {
                    MainThread.BeginInvokeOnMainThread(() =>
                        VideoStatusColor = _statusColours[Status.CommClosed]);
                    return;
                }

                StatusText = "Opening TOM Output...";
                if (!await OpenFeatureCommPort(Feature.TOMOutput))
                {
                    await CloseFeatureCommPort(Feature.TOMInput);
                    AppState.SetVideoCommPortOpen(false);
                    MainThread.BeginInvokeOnMainThread(() =>
                        VideoStatusColor = _statusColours[Status.CommClosed]);
                    return;
                }

                AppState.SetVideoCommPortOpen(true);
                MainThread.BeginInvokeOnMainThread(() =>
                {
                    VideoStatusColor = _statusColours[Status.Disabled];
                    OnPropertyChanged(nameof(CanEnableVideo));
                });
                StatusText = "TOM comm ports opened";
            }
            else if (tomInput.IsCommPortOpen && tomOutput.IsCommPortOpen && !tomInput.IsEnabled)
            {
                // Disabled → Enabled
                if (await EnableVideo())
                {
                    AppState.SetVideoEnabled(true);
                    MainThread.BeginInvokeOnMainThread(() =>
                    {
                        VideoStatusColor = _statusColours[Status.Enabled];
                        OnPropertyChanged(nameof(CanEnableVideo));
                    });
                    StatusText = "Video enabled";
                }
                else
                {
                    MainThread.BeginInvokeOnMainThread(() =>
                        VideoStatusColor = _statusColours[Status.Disabled]);
              //      StatusText = "Video enable failed";
                }
            }
            else if (tomInput.IsCommPortOpen && tomInput.IsEnabled)
            {
                // Enabled → Disabled
                await DisableVideo();           // best-effort
                AppState.SetVideoEnabled(false);

                StatusText = "Closing TOM Output...";
                await CloseFeatureCommPort(Feature.TOMOutput);
                tomOutput.IsCommPortOpen = false;
                tomOutput.IsEnabled = false;
                AppState.UpdateFeature(tomOutput);

                MainThread.BeginInvokeOnMainThread(() =>
                {
                    VideoStatusColor = _statusColours[Status.Disabled];
                    OnPropertyChanged(nameof(CanEnableVideo));
                });
                StatusText = "Video disabled";
            }
            else if (tomInput.IsCommPortOpen && !tomOutput.IsCommPortOpen && !tomInput.IsEnabled)
            {
                // Disabled (Output already closed) → Off
                StatusText = "Closing TOM Input...";
                await CloseFeatureCommPort(Feature.TOMInput);
                tomInput.IsCommPortOpen = false;
                tomInput.IsEnabled = false;
                AppState.UpdateFeature(tomInput);

                MainThread.BeginInvokeOnMainThread(() =>
                {
                    VideoStatusColor = _statusColours[Status.CommClosed];
                    OnPropertyChanged(nameof(CanEnableVideo));
                });
                StatusText = "TOM comm ports closed";
            }
        }
        finally
        {
            IsBusy = false;
        }
    }

    // -------------------------------------------------------------------------
    // ToggleRotatorEnable
    //
    // State machine:
    //   CommClosed → CommOpen → Enabled → Disabled → CommClosed → …
    // -------------------------------------------------------------------------

    [RelayCommand]
    private async Task ToggleRotatorEnable()
    {
        var feature = AppState.GetFeatureByName(Feature.RotatorName);
        if (feature is null || !feature.IsFitted) return;

        IsBusy = true;
        try
        {
            if (!feature.IsCommPortOpen)
            {
                // CommClosed → CommOpen
                if (await OpenFeatureCommPort(Feature.RotatorName))
                {
                    feature.IsCommPortOpen = true;
                    AppState.UpdateFeature(feature);
                    MainThread.BeginInvokeOnMainThread(() =>
                        RotatorStatusColor = _statusColours[Status.CommOpen]);
                    StatusText = "Rotator comm port opened";
                }
                else
                {
                    MainThread.BeginInvokeOnMainThread(() =>
                        RotatorStatusColor = _statusColours[Status.CommClosed]);
                }
            }
            else if (feature.IsCommPortOpen && !feature.IsEnabled && !RotatorPreviouslyEnabled)
            {
                // CommOpen → Enabled
                if (await EnableRotator())
                {
                    feature.IsEnabled = true;
                    AppState.UpdateFeature(feature);
                    AppState.SetRotatorEnabled(true);
                    RotatorPreviouslyEnabled = true;
                    MainThread.BeginInvokeOnMainThread(() =>
                        RotatorStatusColor = _statusColours[Status.Enabled]);
                    StatusText = "Rotator enabled";
                }
            }
            else if (feature.IsEnabled && RotatorPreviouslyEnabled)
            {
                // Enabled → Disabled
                feature.IsEnabled = false;
                AppState.UpdateFeature(feature);
                AppState.SetRotatorEnabled(false);
                MainThread.BeginInvokeOnMainThread(() =>
                    RotatorStatusColor = _statusColours[Status.Disabled]);
                StatusText = "Rotator disabled";
            }
            else
            {
                // Disabled → CommClosed
                if (await CloseFeatureCommPort(Feature.RotatorName))
                {
                    feature.IsEnabled = false;
                    feature.IsCommPortOpen = false;
                    RotatorPreviouslyEnabled = false;
                    AppState.UpdateFeature(feature);
                    MainThread.BeginInvokeOnMainThread(() =>
                        RotatorStatusColor = _statusColours[Status.CommClosed]);
                    StatusText = "Rotator comm port closed";
                }
            }
        }
        finally
        {
            IsBusy = false;
        }
    }

    // -------------------------------------------------------------------------
    // Navigation commands
    // -------------------------------------------------------------------------

    [RelayCommand]
    private async Task Video()
    {
        if (AppState.IsNotConnected) return;

        IsBusy = true;
        try
        {
            StatusText = "Checking video streams...";
            var result = await _dispatcher.SendAndWaitAsync(CameraFeature, "CHECK MTX STREAMS", "", _timeout);

            if (!IsSuccessResponse(result))
            {
                StatusText = "Streams not ready — attempting to start...";
                var discoverJson = JsonSerializer.Serialize(new CameraDiscoverRequest { AutoAdd = true });

                var discoverResult = await _dispatcher.SendAndWaitAsync(CameraFeature, "DISCOVER", discoverJson, _timeout);
                if (discoverResult is null)
                {
                    StatusText = "Failed to start video streams";
                    return;
                }

                StatusText = "Verifying streams...";
                var verifyResult = await _dispatcher.SendAndWaitAsync(CameraFeature, "CHECK MTX STREAMS", "", _timeout);
                if (!IsSuccessResponse(verifyResult))
                {
                    StatusText = "Video streams failed to start";
                    return;
                }
            }

            StatusText = "Video streams ready";
            StatusText = "Opening FLIR control port...";
            if (await _dispatcher.SendAndWaitAsync(SerialFeature, "OPEN", "TOM FLIR", _timeout) is null)
            {
                StatusText = "FLIR control port open failed";
                return;
            }

            StatusText = string.Empty;
            await Shell.Current.GoToAsync(nameof(PeriscopePage));
        }
        finally
        {
            IsBusy = false;
        }
    }


    private static bool IsSuccessResponse(string? response)
    {
        if (response is null) return false;
        try
        {
            using var doc = JsonDocument.Parse(response);
            return doc.RootElement.TryGetProperty("ok", out var prop)
                   && prop.GetBoolean();
        }
        catch
        {
            return false;
        }
    }

    [RelayCommand]
    private async Task Rotator()
    {
        _rotatorViewModel.UpdateRotatorSettings();
        await Shell.Current.GoToAsync(nameof(RotatorPage));
    }

    [RelayCommand]
    private async Task Settings()
    {
        var popup = new PasswordPopup();
        await Shell.Current.CurrentPage.ShowPopupAsync(popup);

        if (popup.EnteredPassword is null) { StatusText = string.Empty; return; }

        if (popup.EnteredPassword.Equals(string.Empty))
        {
            StatusText = "Incorrect settings password entered";
            return;
        }

        if (popup.EnteredPassword.Equals(AppState.ConfigScreenPassword))
        {
            StatusText = string.Empty;
            await Shell.Current.GoToAsync(nameof(ConfigMenuPage));
        }
        else
        {
            StatusText = "Incorrect settings password entered";
        }
    }

    [RelayCommand]
    private void ToggleTheme(bool value)
    {
        IsDarkTheme = value;
        Application.Current.UserAppTheme = value ? AppTheme.Dark : AppTheme.Light;
    }

    // -------------------------------------------------------------------------
    // Initialisation helpers
    // -------------------------------------------------------------------------

    public async Task ButtonLoaded()
    {
        if (_sqliteService.ConfigLoadedError)
        {
            await _alertService.ShowAlertAsync(
                "Error",
                $"Failed To Load Configuration File, Failed To Load Default Settings, {_sqliteService.LastError}",
                "OK");
            return;
        }

        if (_sqliteService.DefaultsLoaded)
        {
            await _alertService.ShowAlertAsync(
                "Warning",
                "Failed To Load Configuration File, Restoring Default Settings",
                "OK");
            _sqliteService.DefaultsLoaded = false;
        }
    }

    public async Task GetConfig()
    {
        if (!await _sqliteService.GetConfigAsync())
        {
            if (await _sqliteService.SetDefaultConfig())
            {
                _sqliteService.DefaultsLoaded = true;
                StatusText = "Default Settings Loaded";
            }
        }
        else
        {
            AppState.SnapShotPath = _sqliteService.config.SnapShotPath;
            AppState.ConfigScreenPassword = _sqliteService.config.ConfigScreenPassword;
            Models.Rotator.MinRotatorValue = _sqliteService.config.MinRotatorValue;
            Models.Rotator.MaxRotatorValue = _sqliteService.config.MaxRotatorValue;
            Models.Rotator.AdjustValue = _sqliteService.config.AdjustValue;
            Models.RaspberryPi.UserName = _sqliteService.config.PiUserName;
            Models.RaspberryPi.Password = _sqliteService.config.PiPassword;
        }
    }

    // -------------------------------------------------------------------------
    // Feature comm-port helpers (thin wrappers around dispatcher)
    // -------------------------------------------------------------------------

    private async Task<bool> OpenFeatureCommPort(string featureName)
    {
        StatusText = $"Opening {featureName}...";

        bool ok = await _dispatcher.SendAndWaitAsync(
            SerialFeature, "OPEN", featureName, _timeout) is not null;

        StatusText = ok
            ? $"{featureName} comm port opened"
            : $"{featureName} comm port open failed";

        return ok;
    }

    private async Task<bool> CloseFeatureCommPort(string featureName)
    {
        StatusText = $"Closing {featureName}...";

        bool ok = await _dispatcher.SendAndWaitAsync(
            SerialFeature, "CLOSE", featureName, _timeout) is not null;

        StatusText = ok
            ? $"{featureName} comm port closed"
            : $"{featureName} comm port close failed";

        return ok;
    }

    // -------------------------------------------------------------------------
    // Domain-level enable / disable helpers
    // -------------------------------------------------------------------------

    private async Task<bool> EnableVideo()
    {
        StatusText = "Checking FFmpeg...";
        string result = await _dispatcher.SendAndWaitAsync(CameraFeature, "CHECK FFMPEG", "", _timeout);
        if (result is null)
        {
            StatusText = "Enable failed — FFmpeg not available";
            return false;
        }

        StatusText = "Checking MediaMTX...";
        result = await _dispatcher.SendAndWaitAsync(CameraFeature, "CHECK MTX VERSION", "", _timeout);
        if (result is null)
        {
            StatusText = "Enable failed — MediaMTX not available";
            return false;
        }

        // TOM power-on: send on "TOM Input", wait for push on "TOM Output"
        if (await _dispatcher.SendAndWaitForPushAsync(
                Feature.TOMInput, "WRITE TEXT", TOMCommands.TurnOnAllSystemsCommand,
                Feature.TOMOutput, IsTomPowerOn,
                _timeout) is null)
        {
            StatusText = "Enable failed — TOM did not confirm power on";
            return false;
        }

        return true;
    }

    private async Task<bool> DisableVideo()
    {
        // best-effort; caller decides how to handle false
        bool ok = await _dispatcher.SendAndWaitForPushAsync(
            Feature.TOMInput, "WRITE TEXT", TOMCommands.TurnOffAllSystemsCommand,
            Feature.TOMOutput, IsTomPowerOff,
            _timeout) is null;

        if (!ok)
            StatusText = "Warning — TOM did not confirm power off";

        return ok;
    }

    private async Task<bool> EnableRotator()
    {

        if (await _rotatorViewModel.EnableRotatorAsync())
        {

            return true;
        }
        StatusText = "Rotator Enable failed";
        return false;

        //// Step 1: set speed
        //if (!await _dispatcher.SendAndWaitForPushAsync(
        //        Feature.RotatorName, "WRITE TEXT",
        //        Models.Rotator.GenerateSetSpeedCommandString(),
        //         Feature.RotatorName,
        //        response => response.Contains("MSP"),
        //        _timeout))
        //{
        //    StatusText = "Rotator failed to respond to set speed command";
        //    return false;
        //}

        //// Step 2: query firmware (confirms comms are healthy)
        //if (!await _dispatcher.SendAndWaitForPushAsync(
        //        Feature.RotatorName, "WRITE TEXT",
        //        Models.Rotator.GetFirmwareVersion,
        //        Feature.RotatorName,
        //        response => response.Contains("MRV"),
        //        _timeout))
        //{
        //    StatusText = "Rotator failed to respond to firmware version request";
        //    return false;
        //}
        //            return true;

    }

    // -------------------------------------------------------------------------
    // LIST REGISTERED query (uses a local one-shot TCS, not the dispatcher,
    // because the response shape is a different JSON type, not CommandResponse)
    // -------------------------------------------------------------------------

    private async Task<HashSet<string>> QuerySerialRegisteredAsync()
    {
        const string command = "LIST REGISTERED";
        var tcs = new TaskCompletionSource<string?>();

        void Handler(object r, TcpDataReceivedMessage msg)
        {
            if (msg.Value.Function.Equals(SerialFeature) &&
                msg.Value.Command.Equals(command))
                tcs.TrySetResult(msg.Value.Data);
        }

        _messengerService.Register<TcpDataReceivedMessage>(tcs, Handler);

        try
        {
            bool sent = await _tcpService.SendCommandAsync(
                new TCPMessageBody<string>(SerialFeature, command, string.Empty),
                CancellationToken.None);

            if (!sent) return new HashSet<string>();

            var completed = await Task.WhenAny(tcs.Task, Task.Delay(_timeout));
            if (completed != tcs.Task) return new HashSet<string>();

            var json = tcs.Task.Result;
            if (string.IsNullOrWhiteSpace(json)) return new HashSet<string>();

            var response = JsonSerializer.Deserialize<ListRegisteredResponse>(
                json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

            return response?.Ok == true && response.Data is not null
                ? response.Data
                    .Select(e => e.FunctionName)
                    .Where(n => !string.IsNullOrWhiteSpace(n))
                    .ToHashSet()
                : new HashSet<string>();
        }
        catch
        {
            return new HashSet<string>();
        }
        finally
        {
            _messengerService.Unregister<TcpDataReceivedMessage>(tcs);
        }
    }

    // -------------------------------------------------------------------------
    // TOM power-state predicates
    // -------------------------------------------------------------------------

    // $PBLUTP,R,PWR,STAT,1,15,0,2.61*28  → field[5] == "15" means ON
    private static bool IsTomPowerOn(string nmea)
    {
        var parts = nmea.Split(',');
        return parts.Length > 5 && parts[5] == "15";
    }

    // $PBLUTP,R,PWR,STAT,1,0,0,2.61*1C   → field[5] == "0" means OFF
    private static bool IsTomPowerOff(string nmea)
    {
        var parts = nmea.Split(',');
        return parts.Length > 5 && parts[5] == "0";
    }

    // -------------------------------------------------------------------------
    // Nested types
    // -------------------------------------------------------------------------

    public sealed class CameraDiscoverRequest
    {
        public bool AutoAdd { get; init; } = true;
    }

    private sealed class SerialRegisteredEntry
    {
        public string FunctionName { get; init; } = string.Empty;
        public string CurrentPort { get; init; } = string.Empty;
    }

    private sealed class CameraRegisteredEntry
    {
        public string StreamPathName { get; init; } = string.Empty;
    }

    private sealed class ListCameraRegisteredResponse
    {
        public bool Ok { get; init; }
        public List<CameraRegisteredEntry>? Data { get; init; }
    }

    [RelayCommand]
    public async Task SendToGPIO()
    {
        await WriteGpioUartAsync("test");
    }

    private async Task<bool> WriteGpioUartAsync(string text)
    {
        StatusText = $"Sending to {Feature.GpioUart0}...";

        bool ok = await _dispatcher.SendAndWaitAsync(
            Feature.GpioUart0, "WRITE TEXT", text, _timeout) is not null;

        StatusText = ok
            ? $"{Feature.GpioUart0} write sent"
            : $"{Feature.GpioUart0} write failed";

        return ok;
    }

    private async Task<bool> WriteGpioUartAndWaitAsync(string text, Func<string, bool> matchPredicate)
    {
        bool ok = await _dispatcher.SendAndWaitForPushAsync(
            Feature.GpioUart0, "WRITE TEXT", text,
            Feature.GpioUart0, matchPredicate,
            _timeout) is not null;

        if (!ok)
            StatusText = $"{Feature.GpioUart0} did not respond as expected";

        return ok;
    }



}