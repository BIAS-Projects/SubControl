using CommunityToolkit.Maui.Storage;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using Microsoft.Extensions.Logging;
using SubConsole.Models;
using SubControlMAUI.Messages;
using SubControlMAUI.Services;
using SubControlMAUI.ViewModels;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;


namespace SubControlMAUI.ViewModels
{
    /// <summary>
    /// Display-friendly wrapper around a <see cref="RemoteLogFile"/> for binding
    /// in the file picker — keeps formatting concerns (human file size, local
    /// time) out of the service layer's plain data record.
    /// </summary>
    public sealed class LogFileEntry
    {
        public RemoteLogFile Source { get; }
        public string FileName => Source.FileName;
        public string FullPath => Source.FullPath;
        public string DisplayName { get; }

        

        public LogFileEntry(RemoteLogFile source)
        {
            Source = source;
            DisplayName = $"{source.FileName}   ·   {FormatSize(source.SizeBytes)}   ·   {source.ModifiedUtc.ToLocalTime():g}";
        }

        private static string FormatSize(long bytes)
        {
            string[] units = { "B", "KB", "MB", "GB", "TB" };
            double size = bytes;
            int unit = 0;

            while (size >= 1024 && unit < units.Length - 1)
            {
                size /= 1024;
                unit++;
            }

            return unit == 0 ? $"{size:0} {units[unit]}" : $"{size:0.#} {units[unit]}";
        }
    }

    /// <summary>
    /// Browse, download, and tail log files on the Raspberry Pi.
    ///
    /// This page owns its own connect/disconnect controls — it doesn't assume
    /// the Console page has already connected, though it will happily reflect
    /// and reuse an existing connection via the shared <see cref="SshService"/>
    /// singleton if one is already up when the page appears.
    ///
    /// Carries forward the lessons learned building the Console page:
    ///   - Leaving the page (Back button, OS swipe-back, Shell popping it for
    ///     any other reason) disconnects and tears down resources exactly the
    ///     same way the Console page does: an active tail is stopped and any
    ///     in-flight download cancelled *before* the shared connection is torn
    ///     down (both depend on it being alive), then event subscriptions on
    ///     the longer-lived SshService are removed deterministically — never
    ///     left dangling on a dead view model.
    ///   - Every disposable/cancellable field is null-checked before use and
    ///     nulled out immediately after being torn down, so cleanup is safe to
    ///     call more than once and never touches an already-disposed object.
    ///   - The tail output uses the same single, read-only, auto-growing Editor
    ///     pattern as the Console page (multi-line select/copy, no per-row
    ///     CollectionView overhead) and is cleared on the way out so a large
    ///     accumulated buffer doesn't cost layout time on the next visit.
    /// </summary>
    public partial class LogsViewModel : BaseViewModel, IDisposable
    {
        private const int MaxTailLines = 2000;

        // Reasonable Raspberry Pi OS defaults for the quick-fill buttons. These
        // just pre-fill an editable Entry, so getting them exactly right isn't
        // critical — the user can always type or edit the path directly.
        private const string DotNetAppDefaultFolder = "/opt/subconsole/logs";
        private const string MediaMtxDefaultFolder = "/opt/mediamtx";
        private const string FfmpegDefaultFolder = "/tmp"; // ffmpeg has no dedicated log directory by default
        private const string SystemDefaultFolder = "/var/log";

        private readonly SQLiteService _sqliteService;
        private readonly SshService _sshService;
        private readonly IAlertService _alertService;
        private readonly ILogger<LogsViewModel> _logger;

        private readonly Queue<string> _tailLines = new();
        private SshStreamSession? _tailSession;
        private CancellationTokenSource? _downloadCts;

        private bool _disposed;

        private readonly IMessenger _messengerService;
        private readonly CommandDispatcherService _dispatcher;
        private readonly ApplicationStateService _appState;

        private static readonly TimeSpan _tcpTimeout = TimeSpan.FromSeconds(10);

        // Function name used as the Function field in outgoing TCP messages for
        // system-level commands (log level, version, etc). Routed server-side via
        // message.Function.Contains("SYSTEM") — see TcpHostService.
        private static string SystemFeature => nameof(LogsViewModel) + "SYSTEM";


        public LogsViewModel(
            SQLiteService sqliteService,
            SshService sshService,
            IAlertService alertService,
            ILogger<LogsViewModel> logger,
            IMessenger messengerService,
            CommandDispatcherService dispatcher,
            ApplicationStateService applicationStateService)
        {
            Title = "Logs";

            _sqliteService = sqliteService;
            _sshService = sshService;
            _alertService = alertService;
            _logger = logger;
            _messengerService = messengerService;
            _dispatcher = dispatcher;
            _dispatcher.Owner = nameof(LogsViewModel);
            _appState = applicationStateService;

            LogFiles = new ObservableCollection<LogFileEntry>();

            Host = _sqliteService.config?.IPAddress ?? string.Empty;

            LogLevels = new ObservableCollection<string>
    {
        "Verbose", "Debug", "Information", "Warning", "Error", "Fatal"
    };
            SelectedLogLevel = "Information";

            RegisterMessages();

            IsConsoleConnected = _appState.IsConnected;
            _appState.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(ApplicationStateService.IsConnected))
                    IsConsoleConnected = _appState.IsConnected;
            };
        }

        private void RegisterMessages()
        {
            // Route every incoming TCP message through the dispatcher so
            // SendAndWaitAsync (used by UpdateLogLevel) can resolve pending requests.
            // Idempotent — safe to call again after Dispose() unregistered us,
            // e.g. when the page is revisited.
            _messengerService.Unregister<TcpDataReceivedMessage>(this);
            _messengerService.Register<TcpDataReceivedMessage>(this, (_, msg) =>
            {
                _dispatcher.HandleIncoming(
                    msg.Value.Function,
                    msg.Value.Command,
                    msg.Value.Data);
            });
        }

        // ── Connection properties ───────────────────────────────────────────────

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(IsNotConnected))]
        [NotifyCanExecuteChangedFor(nameof(ConnectCommand))]
        [NotifyCanExecuteChangedFor(nameof(DisconnectCommand))]
        [NotifyCanExecuteChangedFor(nameof(RefreshLogFilesCommand))]
        [NotifyCanExecuteChangedFor(nameof(StartTailCommand))]
        [NotifyCanExecuteChangedFor(nameof(DownloadLogCommand))]
        private bool isConnected;

        public bool IsNotConnected => !IsConnected;

        [ObservableProperty]
        private string host = string.Empty;

        [ObservableProperty]
        [NotifyCanExecuteChangedFor(nameof(ConnectCommand))]
        private string userName = "pi";

        [ObservableProperty]
        [NotifyCanExecuteChangedFor(nameof(ConnectCommand))]
        private string password = "1234";

        [ObservableProperty]
        private string statusText = string.Empty;

        // ── Log browsing properties ─────────────────────────────────────────────

        [ObservableProperty]
        [NotifyCanExecuteChangedFor(nameof(RefreshLogFilesCommand))]
        private string logFolderPath = string.Empty;

        [ObservableProperty]
        [NotifyCanExecuteChangedFor(nameof(DownloadLogCommand))]
        [NotifyCanExecuteChangedFor(nameof(StartTailCommand))]
        private LogFileEntry? selectedLogFile;

        [ObservableProperty]
        [NotifyCanExecuteChangedFor(nameof(DownloadLogCommand))]
        private string downloadDestinationFolder = "C:";

        [ObservableProperty]
        [NotifyCanExecuteChangedFor(nameof(StartTailCommand))]
        [NotifyCanExecuteChangedFor(nameof(StopTailCommand))]
        [NotifyCanExecuteChangedFor(nameof(RefreshLogFilesCommand))]
        [NotifyCanExecuteChangedFor(nameof(DownloadLogCommand))]
        private bool isTailing;

        /// <summary>The full tail output as a single block of text, newest line last.</summary>
        [ObservableProperty]
        private string tailOutput = string.Empty;

        public ObservableCollection<LogFileEntry> LogFiles { get; }

        /// <summary>
        /// Whether the shared TCP console connection (to the SubConsole server, as
        /// distinct from the SSH connection used for browsing/tailing log files on
        /// this page) is currently up. The log-level Update button is gated on this.
        /// </summary>
        [ObservableProperty]
        [NotifyCanExecuteChangedFor(nameof(UpdateLogLevelCommand))]
        private bool isConsoleConnected;

        [ObservableProperty]
        private string selectedLogLevel = "Information";

        public ObservableCollection<string> LogLevels { get; }

        // ── Connection commands ──────────────────────────────────────────────────

        [RelayCommand(CanExecute = nameof(CanConnect))]
        private async Task Connect()
        {
            IsBusy = true;
            StatusText = $"Connecting to {Host}…";
            _logger.LogInformation("Attempting SSH connection to {Host} as {User}", Host, UserName);

            try
            {
                await _sshService.ConnectAsync(host: Host, username: UserName, password: Password);

                IsConnected = true;
                StatusText = $"Connected to {Host}";
                _logger.LogInformation("SSH connection to {Host} established", Host);

                // Idempotent: safe even if OnAppearing already subscribed for an
                // existing connection made elsewhere (e.g. the Console page).
                _sshService.ConnectionLost -= OnConnectionLost;
                _sshService.ConnectionLost += OnConnectionLost;


            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "SSH connection to {Host} failed", Host);
                StatusText = "Connection failed";
                await _alertService.ShowAlertAsync("Connection Failed", ex.Message, "OK");
            }
            finally
            {
                IsBusy = false;

                RefreshLogFilesCommand.NotifyCanExecuteChanged();
                StartTailCommand.NotifyCanExecuteChanged();
                DownloadLogCommand.NotifyCanExecuteChanged();
                ConnectCommand.NotifyCanExecuteChanged();
                DisconnectCommand.NotifyCanExecuteChanged();

            }
        }

        private bool CanConnect() =>
            !IsBusy &&
            !IsConnected &&
            !string.IsNullOrWhiteSpace(Host) &&
            !string.IsNullOrWhiteSpace(UserName) &&
            !string.IsNullOrWhiteSpace(Password);

        [RelayCommand(CanExecute = nameof(CanDisconnect))]
        private async Task Disconnect()
        {
            // Both depend on the connection being alive, so tear them down
            // before the connection itself goes away.
            await StopTailIfActiveAsync();
            _downloadCts?.Cancel();

            try
            {
                await _sshService.DisconnectAsync();
                _logger.LogInformation("Disconnected from {Host}", Host);
                StatusText = "Disconnected";
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error while disconnecting from {Host}", Host);
                StatusText = "Disconnect error";
            }
            finally
            {
                IsConnected = false;
            }
        }

        private bool CanDisconnect() => IsConnected;

        // ── Folder quick-fill ────────────────────────────────────────────────

        [RelayCommand]
        private void SelectDotNetFolder() => LogFolderPath = DotNetAppDefaultFolder;

        [RelayCommand]
        private void SelectMediaMtxFolder() => LogFolderPath = MediaMtxDefaultFolder;

        [RelayCommand]
        private void SelectFfmpegFolder() => LogFolderPath = FfmpegDefaultFolder;

        [RelayCommand]
        private void SelectSystemFolder() => LogFolderPath = SystemDefaultFolder;

        // ── Listing ──────────────────────────────────────────────────────────

        [RelayCommand(CanExecute = nameof(CanRefreshLogFiles))]
        private async Task RefreshLogFiles()
        {
            if (string.IsNullOrWhiteSpace(LogFolderPath))
                return;

            IsBusy = true;
            StatusText = $"Listing files in {LogFolderPath}…";
            LogFiles.Clear();
            SelectedLogFile = null;

            _logger.LogInformation("Refreshing log file list for {Folder}", LogFolderPath);

            try
            {
                var files = await _sshService.ListLogFilesAsync(LogFolderPath);

                foreach (var file in files)
                    LogFiles.Add(new LogFileEntry(file));

                StatusText = LogFiles.Count > 0
                    ? $"Found {LogFiles.Count} file(s)"
                    : "No files found in this folder";

                // Files come back newest-first already.
                SelectedLogFile = LogFiles.FirstOrDefault();



                _logger.LogInformation("Listed {Count} log file(s) in {Folder}", LogFiles.Count, LogFolderPath);
            }
            catch (DirectoryNotFoundException ex)
            {
                _logger.LogWarning(ex, "Log folder not found: {Folder}", LogFolderPath);
                StatusText = "Folder not found";
                await _alertService.ShowAlertAsync("Folder Not Found", ex.Message, "OK");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to list log files in {Folder}", LogFolderPath);
                StatusText = "Failed to list files";
                await _alertService.ShowAlertAsync("Error", ex.Message, "OK");
            }
            finally
            {
                IsBusy = false;

                RefreshLogFilesCommand.NotifyCanExecuteChanged();
                StartTailCommand.NotifyCanExecuteChanged();
                DownloadLogCommand.NotifyCanExecuteChanged();
            }
        }

        private bool CanRefreshLogFiles() =>
            IsConnected && !IsBusy && !IsTailing && !string.IsNullOrWhiteSpace(LogFolderPath);

        // ── Download ─────────────────────────────────────────────────────────

        [RelayCommand]
        private async Task BrowseDownloadDestination()
        {
            try
            {
                var result = await FolderPicker.Default.PickAsync(CancellationToken.None);

                if (result.IsSuccessful && result.Folder is not null)
                {
                    DownloadDestinationFolder = result.Folder.Path;
                    _logger.LogInformation("Download destination set to {Path}", DownloadDestinationFolder);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Folder picker error while choosing download destination.");
                StatusText = "Couldn't choose a destination folder";
            }
        }

        [RelayCommand(CanExecute = nameof(CanDownloadLog))]
        private async Task DownloadLog()
        {
            if (SelectedLogFile is null || string.IsNullOrWhiteSpace(DownloadDestinationFolder))
                return;

            var file = SelectedLogFile;
            IsBusy = true;
            StatusText = $"Downloading {file.FileName}…";
            _logger.LogInformation("Downloading {RemotePath} to {LocalFolder}", file.FullPath, DownloadDestinationFolder);

            _downloadCts = new CancellationTokenSource();

            try
            {
                Directory.CreateDirectory(DownloadDestinationFolder);
                var localPath = Path.Combine(DownloadDestinationFolder, file.FileName);

                await _sshService.DownloadFileAsync(file.FullPath, localPath, _downloadCts.Token);

                StatusText = $"Downloaded to {localPath}";
                _logger.LogInformation("Download complete: {LocalPath}", localPath);
            }
            catch (OperationCanceledException)
            {
                StatusText = "Download cancelled";
                _logger.LogWarning("Download cancelled: {RemotePath}", file.FullPath);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to download {RemotePath}", file.FullPath);
                StatusText = "Download failed";
                await _alertService.ShowAlertAsync("Download Failed", ex.Message, "OK");
            }
            finally
            {
                IsBusy = false;
                _downloadCts?.Dispose();
                _downloadCts = null;
            }
        }

        private bool CanDownloadLog() =>
            IsConnected && !IsBusy &&
            SelectedLogFile is not null &&
            !string.IsNullOrWhiteSpace(DownloadDestinationFolder);

        // ── Tail ─────────────────────────────────────────────────────────────

        [RelayCommand(CanExecute = nameof(CanStartTail))]
        private async Task StartTail()
        {
            if (SelectedLogFile is null)
                return;

            // Defensive: shouldn't normally have a session already, but never
            // start a second one on top of a leftover from a prior selection.
            await StopTailIfActiveAsync();

            ClearTail();
            var file = SelectedLogFile;
            AddTailLine($"Tailing {file.FullPath}…", TerminalLineKind.Info);
            _logger.LogInformation("Starting tail of {RemotePath}", file.FullPath);

            _tailSession = _sshService.TailLog(file.FullPath);
            _tailSession.LineReceived += OnTailLineReceived;
            IsTailing = true;
        }

        private bool CanStartTail() =>
            IsConnected && !IsBusy && !IsTailing && SelectedLogFile is not null;



        [RelayCommand(CanExecute = nameof(CanStopTail))]
        private async Task StopTail()
        {
            await StopTailIfActiveAsync();
            AddTailLine("Stopped tailing.", TerminalLineKind.Info);
        }

        private bool CanStopTail() => IsTailing;

        [RelayCommand]
        private void ClearTailView() => ClearTail();

        /// <summary>
        /// Stops and disposes the active tail session, if any. Always null-checks
        /// before touching it and nulls it out immediately after, so this is safe
        /// to call speculatively (e.g. before starting a new tail) and safe to
        /// call more than once during teardown.
        /// </summary>
        private async Task StopTailIfActiveAsync()
        {
            if (_tailSession is null)
                return;

            _tailSession.LineReceived -= OnTailLineReceived;

            try
            {
                await _tailSession.DisposeAsync();
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Error disposing tail session (best-effort).");
            }

            _tailSession = null;
            IsTailing = false;
        }

        private void OnTailLineReceived(string line)
        {
            if (_disposed)
                return;

            MainThread.BeginInvokeOnMainThread(() =>
            {
                if (_disposed)
                    return;

                AddTailLine(line, TerminalLineKind.Stream);
            });
        }

        // ── Navigation ────────────────────────────────────────────────────────

        [RelayCommand]
        private async Task GoBack()
        {
            StatusText = string.Empty;
            if (IsConnected)
                await Disconnect();

            await Shell.Current.GoToAsync("..");
        }

        /// <summary>
        /// Call when the page appears. Reflects whatever connection state the
        /// shared SshService is already in (e.g. connected from the Console
        /// page) and (re-)subscribes to connection-loss notifications. Safe to
        /// call more than once, including after a prior <see cref="Dispose"/> if
        /// this view model turns out to be reused across page visits.
        /// </summary>
        public void OnAppearing()
        {

            Host = _sqliteService.config.IPAddress;
            _disposed = false;

            IsConnected = _sshService.IsConnected;
            if (IsConnected)
            {
                // Reflect the actual connected host rather than whatever's
                // sitting in the (possibly stale, possibly mid-edit) Host entry.
                Host = _sshService.Host;
                StatusText = $"Connected to {Host}";
            }

            LogFolderPath = DotNetAppDefaultFolder;

            RegisterMessages();

            // Idempotent, so it doesn't matter whether Connect() on this page
            // already subscribed for a connection made right here.
            _sshService.ConnectionLost -= OnConnectionLost;
            _sshService.ConnectionLost += OnConnectionLost;

            _logger.LogInformation("Logs page appeared. Connected: {IsConnected}", IsConnected);
        }

        private void OnConnectionLost(Exception ex)
        {
            if (_disposed)
                return;

            MainThread.BeginInvokeOnMainThread(() =>
            {
                if (_disposed)
                    return;

                _logger.LogWarning(ex, "SSH connection to {Host} was lost while viewing logs.", Host);

                IsConnected = false;
                IsBusy = false;
                StatusText = "Connection lost";

                // The service has already torn down the tail's ShellStream along
                // with everything else, so just drop our reference rather than
                // trying to dispose it again.
                if (_tailSession is not null)
                {
                    _tailSession.LineReceived -= OnTailLineReceived;
                    _tailSession = null;
                }
                IsTailing = false;

                _sshService.ConnectionLost -= OnConnectionLost;
            });
        }

        /// <summary>
        /// Unsubscribes from SshService and clears the tail buffer. Guarded so
        /// it's safe to call more than once. This alone does not stop a running
        /// tail or disconnect — call <see cref="Disconnect"/> first if
        /// <see cref="IsConnected"/> (see GoBack / LogsPage.OnDisappearing for
        /// the full, correctly-ordered teardown sequence).
        /// </summary>
        public void Dispose()
        {
            if (_disposed)
                return;

            _disposed = true;

            _downloadCts?.Dispose();
            _downloadCts = null;

            _sshService.ConnectionLost -= OnConnectionLost;
            _messengerService.Unregister<TcpDataReceivedMessage>(this);

            ClearTail();
        }

        //-----------Logs----------------------------

        // ── Server logging level ────────────────────────────────────────────────

        [RelayCommand(CanExecute = nameof(CanUpdateLogLevel))]
        private async Task UpdateLogLevel()
        {
            if (string.IsNullOrWhiteSpace(SelectedLogLevel))
                return;

            IsBusy = true;
            StatusText = $"Setting server log level to {SelectedLogLevel}…";
            _logger.LogInformation("Requesting server log level change to {Level}", SelectedLogLevel);

            try
            {
                var result = await _dispatcher.SendAndWaitAsync(
                    SystemFeature, "LOGGING", SelectedLogLevel, _tcpTimeout);

                if (result is null)
                {
                    _logger.LogWarning(
                        "LOGGING command failed or timed out for level {Level}", SelectedLogLevel);
                    StatusText = "Failed to set log level";
                    await _alertService.ShowAlertAsync(
                        "Error", "The server did not confirm the log level change.", "OK");
                    return;
                }

                _logger.LogInformation("Server log level set to {Level}", SelectedLogLevel);
                StatusText = $"Server log level set to {SelectedLogLevel}";
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to set server log level to {Level}", SelectedLogLevel);
                StatusText = "Failed to set log level";
                await _alertService.ShowAlertAsync("Error", ex.Message, "OK");
            }
            finally
            {
                IsBusy = false;
            }
        }

        private bool CanUpdateLogLevel() => IsConsoleConnected;

        // ── Private helpers ───────────────────────────────────────────────────

        private void ClearTail()
        {
            _tailLines.Clear();
            TailOutput = string.Empty;
        }

        private void AddTailLine(string text, TerminalLineKind kind)
        {
            _tailLines.Enqueue(FormatTailLine(text, kind));

            while (_tailLines.Count > MaxTailLines)
                _tailLines.Dequeue();

            TailOutput = string.Join(Environment.NewLine, _tailLines);
        }

        private static string FormatTailLine(string text, TerminalLineKind kind) => kind switch
        {
            TerminalLineKind.Error => $"✗ {text}",
            TerminalLineKind.Info => $"· {text}",
            TerminalLineKind.Success => $"✓ {text}",
            TerminalLineKind.Stream => text,
            _ => text,
        };
    }
}