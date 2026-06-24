using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using Microsoft.Extensions.Logging;
using SubControlMAUI.Services;
using System;
using System.Collections.ObjectModel;
using System.Threading;
using System.IO;
using System.Threading.Tasks;

namespace SubControlMAUI.ViewModels
{
    /// <summary>
    /// A single line in the output terminal, tagged with its source type for colour-coding.
    /// </summary>
    public partial class TerminalLine : ObservableObject
    {
        public string Text { get; }
        public TerminalLineKind Kind { get; }
        public DateTime Timestamp { get; } = DateTime.Now;

        public TerminalLine(string text, TerminalLineKind kind = TerminalLineKind.Output)
        {
            Text = text;
            Kind = kind;
        }
    }

    public enum TerminalLineKind
    {
        Info,       // status / connection messages  → blue/grey
        Command,    // echoed user input             → yellow
        Output,     // stdout from commands          → white
        Error,      // stderr / failures             → red
        Stream,     // live tail / streaming output  → green
        Success,    // exit-code 0 confirmation      → green-muted
    }

    public partial class PiViewModel : BaseViewModel
    {
        // ── Dependencies ──────────────────────────────────────────────────────
        private readonly IMessenger _messenger;
        private readonly ILogger<PiViewModel> _logger;
        private readonly SQLiteService _sQLiteService;
        private readonly SshService _sshService;

        // ── Streaming session (tail / long-running process) ───────────────────
        private SshStreamSession? _activeStream;
        private CancellationTokenSource? _commandCts;

        // ── Constructor ───────────────────────────────────────────────────────
        public PiViewModel(
            IMessenger messenger,
            ILogger<PiViewModel> logger,
            SQLiteService sQLiteService,
            SshService sshService)
        {
            _logger = logger;
            _messenger = messenger;
            _sQLiteService = sQLiteService;
            _sshService = sshService;

            Title = "Controller Management";
            CommandHistory = new ObservableCollection<string>();
            TerminalLines = new ObservableCollection<TerminalLine>();
        }

        // ── Observable properties ─────────────────────────────────────────────

        [ObservableProperty]
        [NotifyCanExecuteChangedFor(nameof(ConnectCommand))]
        [NotifyCanExecuteChangedFor(nameof(DisconnectCommand))]
        [NotifyCanExecuteChangedFor(nameof(RunCommandCommand))]
        [NotifyCanExecuteChangedFor(nameof(GetSystemInfoCommand))]
        [NotifyCanExecuteChangedFor(nameof(TailLogCommand))]
        [NotifyCanExecuteChangedFor(nameof(StopStreamCommand))]
        [NotifyCanExecuteChangedFor(nameof(DeployMediaMtxCommand))]
        private bool isConnected;

        [ObservableProperty]
        [NotifyCanExecuteChangedFor(nameof(ConnectCommand))]
        private bool isBusy;

        [ObservableProperty]
        [NotifyCanExecuteChangedFor(nameof(ConnectCommand))]
        private string userName = "pi";

        [ObservableProperty]
        [NotifyCanExecuteChangedFor(nameof(ConnectCommand))]
        private string password = "1234";

        [ObservableProperty]
        private string commandInput = string.Empty;

        [ObservableProperty]
        private string logFilePath = "/var/log/syslog";

        [ObservableProperty]
        private bool isStreaming;

        // ── MediaMTX deployment properties ────────────────────────────────────

        [ObservableProperty]
        [NotifyCanExecuteChangedFor(nameof(DeployMediaMtxCommand))]
        private string mediaMtxLocalPath = string.Empty;

        [ObservableProperty]
        private string mediaMtxInstallDir = "/opt/mediamtx";

        [ObservableProperty]
        private string mediaMtxServiceUser = "root";

        [ObservableProperty]
        private int deployProgress;

        [ObservableProperty]
        private string deployStatus = string.Empty;

        [ObservableProperty]
        [NotifyCanExecuteChangedFor(nameof(DeployMediaMtxCommand))]
        private bool isDeploying;

        /// <summary>All lines displayed in the terminal panel.</summary>
        public ObservableCollection<TerminalLine> TerminalLines { get; }

        /// <summary>Previous commands for quick recall.</summary>
        public ObservableCollection<string> CommandHistory { get; }

        private int _historyIndex = -1;

        // ── Connection commands ───────────────────────────────────────────────

        [RelayCommand(CanExecute = nameof(CanConnect))]
        private async Task Connect()
        {
            IsBusy = true;

            _sQLiteService.config.IPAddress = "10.1.16.144";

            AddLine($"Connecting to {_sQLiteService.config.IPAddress} as '{UserName}'…", TerminalLineKind.Info);

            try
            {
                await _sshService.ConnectAsync(
                    host: _sQLiteService.config.IPAddress,
                    username: UserName,
                    password: Password);

                IsConnected = true;
                AddLine($"✓ Connected to {_sQLiteService.config.IPAddress}", TerminalLineKind.Success);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Connection failed");
                AddLine($"✗ Connection failed: {ex.Message}", TerminalLineKind.Error);
            }
            finally
            {
                IsBusy = false;
            }
        }

        private bool CanConnect() =>
            !IsBusy &&
            !IsConnected &&
            !string.IsNullOrWhiteSpace(UserName) &&
            !string.IsNullOrWhiteSpace(Password);

        [RelayCommand(CanExecute = nameof(CanDisconnect))]
        private async Task Disconnect()
        {
            await StopStreamIfActive();

            try
            {
                await _sshService.DisconnectAsync();
                IsConnected = false;
                AddLine("Disconnected.", TerminalLineKind.Info);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Disconnect error");
                AddLine($"Disconnect error: {ex.Message}", TerminalLineKind.Error);
            }
        }

        private bool CanDisconnect() => IsConnected;

        // ── Command execution ─────────────────────────────────────────────────

        [RelayCommand(CanExecute = nameof(CanRunCommand))]
        private async Task RunCommand()
        {
            if (string.IsNullOrWhiteSpace(CommandInput)) return;

            var cmd = CommandInput.Trim();
            CommandInput = string.Empty;

            // Store in history (most recent first, no duplicates)
            if (CommandHistory.Count == 0 || CommandHistory[0] != cmd)
                CommandHistory.Insert(0, cmd);
            _historyIndex = -1;

            AddLine($"$ {cmd}", TerminalLineKind.Command);

            _commandCts = new CancellationTokenSource();
            IsBusy = true;

            try
            {
                var result = await _sshService.ExecuteCommandAsync(cmd, _commandCts.Token);

                if (!string.IsNullOrWhiteSpace(result.StdOut))
                {
                    foreach (var line in result.StdOut.Split('\n', StringSplitOptions.RemoveEmptyEntries))
                        AddLine(line, TerminalLineKind.Output);
                }

                if (!string.IsNullOrWhiteSpace(result.StdErr))
                {
                    foreach (var line in result.StdErr.Split('\n', StringSplitOptions.RemoveEmptyEntries))
                        AddLine(line, TerminalLineKind.Error);
                }

                AddLine(
                    $"[exit {result.ExitCode} | {result.Duration.TotalMilliseconds:N0}ms]",
                    result.Succeeded ? TerminalLineKind.Success : TerminalLineKind.Error);
            }
            catch (OperationCanceledException)
            {
                AddLine("Command cancelled.", TerminalLineKind.Info);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Command execution error");
                AddLine($"Error: {ex.Message}", TerminalLineKind.Error);
            }
            finally
            {
                IsBusy = false;
                _commandCts?.Dispose();
                _commandCts = null;
            }
        }

        private bool CanRunCommand() => IsConnected && !IsStreaming;

        // ── System info shortcut ──────────────────────────────────────────────

        [RelayCommand(CanExecute = nameof(CanRunCommand))]
        private async Task GetSystemInfo()
        {
            AddLine("Fetching system info…", TerminalLineKind.Info);
            IsBusy = true;
            try
            {
                var result = await _sshService.GetSystemInfoAsync();
                foreach (var line in result.StdOut.Split('\n', StringSplitOptions.RemoveEmptyEntries))
                    AddLine(line, TerminalLineKind.Output);
            }
            catch (Exception ex)
            {
                AddLine($"Error: {ex.Message}", TerminalLineKind.Error);
            }
            finally { IsBusy = false; }
        }

        // ── Log tailing (streaming) ───────────────────────────────────────────

        [RelayCommand(CanExecute = nameof(CanTailLog))]
        private async Task TailLog()
        {
            await StopStreamIfActive();

            AddLine($"▶ Tailing {LogFilePath}…", TerminalLineKind.Info);
            IsStreaming = true;

            _activeStream = _sshService.TailLog(LogFilePath);
            _activeStream.LineReceived += OnStreamLine;
        }

        private bool CanTailLog() => IsConnected && !IsStreaming;

        [RelayCommand(CanExecute = nameof(CanStopStream))]
        private async Task StopStream()
        {
            await StopStreamIfActive();
            AddLine("◼ Stream stopped.", TerminalLineKind.Info);
        }

        private bool CanStopStream() => IsStreaming;

        private async Task StopStreamIfActive()
        {
            if (_activeStream is not null)
            {
                _activeStream.LineReceived -= OnStreamLine;
                await _activeStream.DisposeAsync();
                _activeStream = null;
                IsStreaming = false;
            }
        }

        private void OnStreamLine(string line)
        {
            // Marshal to the UI thread
            MainThread.BeginInvokeOnMainThread(() => AddLine(line, TerminalLineKind.Stream));
        }

        // ── Terminal helpers ──────────────────────────────────────────────────

        [RelayCommand]
        private void ClearTerminal() => TerminalLines.Clear();

        [RelayCommand]
        private void HistoryUp()
        {
            if (CommandHistory.Count == 0) return;
            _historyIndex = Math.Min(_historyIndex + 1, CommandHistory.Count - 1);
            CommandInput = CommandHistory[_historyIndex];
        }

        [RelayCommand]
        private void HistoryDown()
        {
            if (_historyIndex <= 0) { _historyIndex = -1; CommandInput = string.Empty; return; }
            _historyIndex--;
            CommandInput = CommandHistory[_historyIndex];
        }

        // ── MediaMTX deployment ──────────────────────────────────────────────

        [RelayCommand(CanExecute = nameof(CanDeployMediaMtx))]
        private async Task DeployMediaMtx()
        {
            IsDeploying = true;
            DeployProgress = 0;
            DeployStatus = string.Empty;

            var progress = new Progress<(string Step, int Percent)>(report =>
            {
                MainThread.BeginInvokeOnMainThread(() =>
                {
                    DeployProgress = report.Percent;
                    DeployStatus = report.Step;
                    AddLine(report.Step, TerminalLineKind.Info);
                });
            });

            try
            {
                AddLine($"▶ Deploying MediaMTX from: {Path.GetFileName(MediaMtxLocalPath)}", TerminalLineKind.Info);

                await _sshService.DeployMediaMtxAsync(
                    localTarGzPath: MediaMtxLocalPath,
                    remoteInstallDir: MediaMtxInstallDir,
                    serviceUser: MediaMtxServiceUser,
                    progress: progress);

                AddLine("✓ MediaMTX deployed, enabled on boot, and started.", TerminalLineKind.Success);

                // Start tailing the service log automatically
                await StopStreamIfActive();
                LogFilePath = "/var/log/syslog";
                AddLine("▶ Tailing syslog for MediaMTX output…", TerminalLineKind.Info);
                IsStreaming = true;
                _activeStream = _sshService.StartStreamingCommand(
                    "sudo journalctl -u mediamtx.service -f -n 30 --no-pager");
                _activeStream.LineReceived += OnStreamLine;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "MediaMTX deployment failed");
                AddLine($"✗ Deployment failed: {ex.Message}", TerminalLineKind.Error);
                DeployStatus = "Deployment failed";
            }
            finally
            {
                IsDeploying = false;
            }
        }

        private bool CanDeployMediaMtx() =>
            IsConnected &&
            !IsDeploying &&
            !string.IsNullOrWhiteSpace(MediaMtxLocalPath) &&
            File.Exists(MediaMtxLocalPath);

        [RelayCommand]
        private async Task PickMediaMtxFile()
        {
            try
            {
                var result = await FilePicker.Default.PickAsync(new PickOptions
                {
                    PickerTitle = "Select MediaMTX .tar.gz archive",
                    FileTypes = new FilePickerFileType(new Dictionary<DevicePlatform, IEnumerable<string>>
                    {
                        // WinUI only accepts single-dot extensions — .gz covers .tar.gz files
                        { DevicePlatform.WinUI,       new[] { ".gz", ".tgz" } },
                        { DevicePlatform.Android,     new[] { "application/gzip", "application/x-gzip", "application/x-tar" } },
                        { DevicePlatform.iOS,         new[] { "public.tar-archive", "org.gnu.gnu-zip-archive" } },
                        { DevicePlatform.MacCatalyst, new[] { "public.tar-archive", "org.gnu.gnu-zip-archive" } },
                    })
                });

                if (result is not null)
                {
                    MediaMtxLocalPath = result.FullPath;
                    AddLine($"Selected: {result.FileName}", TerminalLineKind.Info);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "File picker error");
                AddLine($"File picker error: {ex.Message}", TerminalLineKind.Error);
            }
        }

        // ── Navigation ────────────────────────────────────────────────────────

        [RelayCommand]
        private async Task GoBack()
        {
            await StopStreamIfActive();
            await Shell.Current.GoToAsync("..");
        }

        // ── Private helpers ───────────────────────────────────────────────────

        private void AddLine(string text, TerminalLineKind kind = TerminalLineKind.Output)
        {
            // Always called on the UI thread (callers from background threads use BeginInvokeOnMainThread)
            TerminalLines.Add(new TerminalLine(text, kind));

            // Cap history to avoid runaway memory on long sessions
            while (TerminalLines.Count > 2000)
                TerminalLines.RemoveAt(0);
        }
    }
}