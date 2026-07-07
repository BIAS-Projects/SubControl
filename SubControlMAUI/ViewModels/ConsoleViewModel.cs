using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using SubControlMAUI.Services;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Threading;
using System.Threading.Tasks;

namespace SubControlMAUI.ViewModels
{
    /// <summary>
    /// Provides an interactive SSH terminal: connect, disconnect, run commands and
    /// view their output. This supersedes the SSH portion of PiViewModel, which is
    /// being retired; deployment-specific functionality is intentionally not carried
    /// over here.
    /// </summary>
    public partial class ConsoleViewModel : BaseViewModel
    {
        private const int MaxTerminalLines = 2000;

        private readonly SQLiteService _sqliteService;
        private readonly IAlertService _alertService;
        private readonly SshService _sshService;
        private readonly ILogger<ConsoleViewModel> _logger;

        // Backing store for the terminal text block. A plain Queue<string> keeps
        // memory bounded while letting us rebuild the single displayed string
        // cheaply; the display itself is one read-only, multi-line Editor rather
        // than a list of controls, so the user can drag-select across lines and
        // copy more than one line at a time.
        private readonly Queue<string> _terminalLines = new();

        private CancellationTokenSource? _commandCts;
        private int _historyIndex = -1;

        public ConsoleViewModel(
            SQLiteService sqliteService,
            IAlertService alertService,
            SshService sshService,
            ILogger<ConsoleViewModel> logger)
        {
            Title = "Console";

            _sqliteService = sqliteService;
            _alertService = alertService;
            _sshService = sshService;
            _logger = logger;

            CommandHistory = new ObservableCollection<string>();

            // The host is managed via the app's saved configuration; only the
            // credentials are edited here, matching how PiViewModel worked.
            Host = _sqliteService.config?.IPAddress ?? string.Empty;
        }

        // ── Observable properties ─────────────────────────────────────────────

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(IsNotConnected))]
        [NotifyCanExecuteChangedFor(nameof(ConnectCommand))]
        [NotifyCanExecuteChangedFor(nameof(DisconnectCommand))]
        [NotifyCanExecuteChangedFor(nameof(RunCommandCommand))]
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
        private string commandInput = string.Empty;

        [ObservableProperty]
        private string statusText = string.Empty;

        /// <summary>
        /// The full terminal output as a single block of text, newest line last.
        /// Bound to one read-only Editor so the user can select and copy across
        /// multiple lines, unlike a per-line CollectionView.
        /// </summary>
        [ObservableProperty]
        private string terminalOutput = string.Empty;

        /// <summary>Previous commands for quick recall (most recent first).</summary>
        public ObservableCollection<string> CommandHistory { get; }

        // ── Connection commands ───────────────────────────────────────────────

        [RelayCommand(CanExecute = nameof(CanConnect))]
        private async Task Connect()
        {
            IsBusy = true;
            StatusText = $"Connecting to {Host}…";
            _logger.LogInformation("Attempting SSH connection to {Host} as {User}", Host, UserName);
            AddLine($"Connecting to {Host} as '{UserName}'…", TerminalLineKind.Info);

            try
            {
                await _sshService.ConnectAsync(
                    host: Host,
                    username: UserName,
                    password: Password);

                IsConnected = true;
                StatusText = $"Connected to {Host}";
                _logger.LogInformation("SSH connection to {Host} established", Host);
                AddLine($"Connected to {Host}", TerminalLineKind.Success);

                // Surface output the interactive shell produces on its own —
                // background job notices, MOTD, etc. — rather than dropping it.
                _sshService.InteractiveAsyncOutputReceived += OnAsyncOutputReceived;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "SSH connection to {Host} failed", Host);
                StatusText = "Connection failed";
                AddLine($"Connection failed: {ex.Message}", TerminalLineKind.Error);
                await _alertService.ShowAlertAsync("Connection Failed", ex.Message, "OK");
            }
            finally
            {
                IsBusy = false;
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
            // A command may still be running; cancel it before tearing down the connection.
            _commandCts?.Cancel();
            _sshService.InteractiveAsyncOutputReceived -= OnAsyncOutputReceived;

            try
            {
                await _sshService.DisconnectAsync();
                _logger.LogInformation("SSH connection to {Host} closed", Host);
                AddLine("Disconnected.", TerminalLineKind.Info);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error while disconnecting SSH session from {Host}", Host);
                AddLine($"Disconnect error: {ex.Message}", TerminalLineKind.Error);
            }
            finally
            {
                IsConnected = false;
                StatusText = "Disconnected";
            }
        }

        private bool CanDisconnect() => IsConnected;

        // ── Command execution ─────────────────────────────────────────────────

        [RelayCommand(CanExecute = nameof(CanRunCommand))]
        private async Task RunCommand()
        {
            if (string.IsNullOrWhiteSpace(CommandInput))
                return;

            var cmd = CommandInput.Trim();
            CommandInput = string.Empty;

            // Store in history (most recent first, no consecutive duplicates)
            if (CommandHistory.Count == 0 || CommandHistory[0] != cmd)
                CommandHistory.Insert(0, cmd);
            _historyIndex = -1;

            AddLine(cmd, TerminalLineKind.Command);
            _logger.LogInformation("Executing SSH command on {Host}: {Command}", Host, cmd);

            _commandCts = new CancellationTokenSource();
            IsBusy = true;

            try
            {
                var result = await _sshService.ExecuteInteractiveCommandAsync(cmd, _commandCts.Token);

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

                _logger.LogInformation(
                    "Command completed on {Host}: {Command} (exit {ExitCode}, {Duration}ms)",
                    Host, cmd, result.ExitCode, result.Duration.TotalMilliseconds);
            }
            catch (OperationCanceledException)
            {
                _logger.LogWarning("Command cancelled on {Host}: {Command}", Host, cmd);
                AddLine("Command cancelled.", TerminalLineKind.Info);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Command execution failed on {Host}: {Command}", Host, cmd);
                AddLine($"Error: {ex.Message}", TerminalLineKind.Error);
            }
            finally
            {
                IsBusy = false;
                _commandCts?.Dispose();
                _commandCts = null;
            }
        }

        private bool CanRunCommand() => IsConnected && !IsBusy;

        // ── Terminal helpers ──────────────────────────────────────────────────

        [RelayCommand]
        private void ClearTerminal()
        {
            _terminalLines.Clear();
            TerminalOutput = string.Empty;
        }

        [RelayCommand]
        private void HistoryUp()
        {
            if (CommandHistory.Count == 0)
                return;

            _historyIndex = Math.Min(_historyIndex + 1, CommandHistory.Count - 1);
            CommandInput = CommandHistory[_historyIndex];
        }

        [RelayCommand]
        private void HistoryDown()
        {
            if (_historyIndex <= 0)
            {
                _historyIndex = -1;
                CommandInput = string.Empty;
                return;
            }

            _historyIndex--;
            CommandInput = CommandHistory[_historyIndex];
        }

        // ── Navigation ────────────────────────────────────────────────────────

        [RelayCommand]
        private async Task GoBack()
        {
            if (IsConnected)
                await Disconnect();

            await Shell.Current.GoToAsync("..");
        }

        // ── Private helpers ───────────────────────────────────────────────────

        /// <summary>
        /// Renders output the interactive shell produced on its own — with no
        /// command awaiting it — as info lines in the terminal, so it's visible
        /// but clearly distinguished from a command's actual output.
        /// </summary>
        private void OnAsyncOutputReceived(string text)
        {
            MainThread.BeginInvokeOnMainThread(() =>
            {
                foreach (var line in text.Split('\n', StringSplitOptions.RemoveEmptyEntries))
                {
                    var trimmed = line.TrimEnd('\r');
                    if (!string.IsNullOrWhiteSpace(trimmed))
                        AddLine(trimmed, TerminalLineKind.Info);
                }
            });
        }

        /// <summary>
        /// Appends a formatted line to the terminal text block and rebuilds the
        /// displayed string. Always called on the UI thread since none of the
        /// callers above hop to a background thread before invoking it.
        /// </summary>
        private void AddLine(string text, TerminalLineKind kind = TerminalLineKind.Output)
        {
            _terminalLines.Enqueue(FormatLine(text, kind));

            // Cap history to avoid runaway memory on long sessions.
            while (_terminalLines.Count > MaxTerminalLines)
                _terminalLines.Dequeue();

            TerminalOutput = string.Join(Environment.NewLine, _terminalLines);
        }

        /// <summary>
        /// Prefixes a line with a short, kind-specific marker. A single Editor
        /// can't colour individual lines the way the old per-row CollectionView
        /// did, so the marker carries that meaning instead.
        /// </summary>
        private static string FormatLine(string text, TerminalLineKind kind) => kind switch
        {
            TerminalLineKind.Command => $"$ {text}",
            TerminalLineKind.Error => $"✗ {text}",
            TerminalLineKind.Info => $"· {text}",
            TerminalLineKind.Success => $"✓ {text}",
            TerminalLineKind.Stream => $"| {text}",
            _ => $"  {text}",
        };
    }
}