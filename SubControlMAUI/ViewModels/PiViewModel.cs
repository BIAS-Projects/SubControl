using CommunityToolkit.Maui.Storage;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using SubControlMAUI.Services;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace SubControlMAUI.ViewModels
{
    public partial class PiViewModel : BaseViewModel, IDisposable
    {
        private const int MaxTerminalLines = 2000;

        // ── Dependencies ──────────────────────────────────────────────────────
        private readonly IMessenger _messenger;
        private readonly ILogger<PiViewModel> _logger;
        private readonly SQLiteService _sqliteService;
        private readonly SshService _sshService;

        // ── Streaming session (used by deploy commands to auto-tail the
        //    resulting service's journal once it's up) ─────────────────────────
        private SshStreamSession? _activeStream;

        // ── Terminal output buffer (single Editor, newest line last — same
        //    pattern as LogsViewModel.TailOutput) ──────────────────────────────
        private readonly Queue<string> _terminalLines = new();

        private bool _disposed;

        // ── Constructor ───────────────────────────────────────────────────────
        public PiViewModel(
            IMessenger messenger,
            ILogger<PiViewModel> logger,
            SQLiteService sqliteService,
            SshService sshService)
        {
            _logger = logger;
            _messenger = messenger;
            _sqliteService = sqliteService;
            _sshService = sshService;

            Title = "Controller Management";
        }

        // ── Observable properties ─────────────────────────────────────────────

        [ObservableProperty]
        [NotifyCanExecuteChangedFor(nameof(ConnectCommand))]
        [NotifyCanExecuteChangedFor(nameof(DisconnectCommand))]
        [NotifyCanExecuteChangedFor(nameof(DeployMediaMtxCommand))]
        [NotifyCanExecuteChangedFor(nameof(DeployDotNetAppCommand))]
        [NotifyCanExecuteChangedFor(nameof(DeployFfmpegCommand))]
        [NotifyPropertyChangedFor(nameof(IsNotConnected))]
        private bool isConnected;

        public bool IsNotConnected => !IsConnected;

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
        private bool isStreaming;

        /// <summary>The full terminal output as a single block of text, newest line last.</summary>
        [ObservableProperty]
        private string terminalOutput = string.Empty;

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

        // ── FFmpeg deployment properties ──────────────────────────────────────

        [ObservableProperty]
        [NotifyCanExecuteChangedFor(nameof(DeployFfmpegCommand))]
        private string ffmpegLocalPath = string.Empty;

        [ObservableProperty]
        private string ffmpegInstallDir = "/usr/local/bin";

        [ObservableProperty]
        private string ffmpegServiceUser = "root";

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(FfmpegDeployProgressConverted))]
        private double ffmpegDeployProgress;

        // Converts double (0-100) to native MAUI ProgressBar range (0.0 – 1.0)
        public double FfmpegDeployProgressConverted => FfmpegDeployProgress / 100.0;

        [ObservableProperty]
        private string ffmpegDeployStatus = string.Empty;

        [ObservableProperty]
        [NotifyCanExecuteChangedFor(nameof(DeployFfmpegCommand))]
        private bool isFfmpegDeploying;

        [ObservableProperty]
        [NotifyCanExecuteChangedFor(nameof(DeployDotNetAppCommand))]
        private string dotNetLocalPath = "D:\\repository\\SubControl\\SubConsole\\bin\\Debug\\net10.0\\publish\\linux-arm64";

        [ObservableProperty]
        private string dotNetInstallDir = "/opt/subconsole";

        [ObservableProperty]
        private string dotNetExecutableName = "SubConsole";

        [ObservableProperty]
        private string dotNetServiceName = "subconsole";

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(DotNetDeployProgressConverted))]
        private double dotNetDeployProgress;

        // Converts double (0-100) from script callback payload to native MAUI ProgressBar range (0.0 - 1.0)
        public double DotNetDeployProgressConverted => DotNetDeployProgress / 100.0;

        [ObservableProperty]
        private string dotNetDeployStatus = string.Empty;

        [ObservableProperty]
        [NotifyCanExecuteChangedFor(nameof(DeployDotNetAppCommand))]
        private bool isDotNetDeploying;

        [ObservableProperty]
        [NotifyCanExecuteChangedFor(nameof(ConfigureUartAndRebootCommand))]
        private bool _isConfiguringUart;

        [ObservableProperty]
        private string _uartConfigStatus = string.Empty;

        [ObservableProperty]
        private string host = string.Empty;

        // ── Connection commands ───────────────────────────────────────────────

        [RelayCommand(CanExecute = nameof(CanConnect))]
        private async Task Connect()
        {
            IsBusy = true;

            AddLine($"Connecting to {_sqliteService.config.IPAddress} as '{UserName}'…", TerminalLineKind.Info);

            try
            {
                await _sshService.ConnectAsync(
                    host: _sqliteService.config.IPAddress,
                    username: UserName,
                    password: Password);

                IsConnected = true;
                AddLine($"✓ Connected to {_sqliteService.config.IPAddress}", TerminalLineKind.Success);
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

        // ── Terminal helpers ──────────────────────────────────────────────────

        [RelayCommand]
        private void ClearTerminal() => ClearTerminalOutput();

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

                // ── Enable the API programmatically via SSH ──
                AddLine("▶ Configuring MediaMTX API...", TerminalLineKind.Info);

                string configPath = Path.Combine(MediaMtxInstallDir, "mediamtx.yml").Replace('\\', '/');
                // Use sed to turn api: false into api: true, and restart the service if it's already running
                // Matches 'api: false' or 'api: no' even if spaces exist around the colon
                string enableApiCommand = $"sudo sed -i 's/^api:\\s*\\(false\\|no\\)/api: true/' {configPath} && sudo systemctl restart mediamtx.service";


                var result = await _sshService.ExecuteCommandAsync(enableApiCommand);
                AddLine("✓ MediaMTX API settings file upadted", TerminalLineKind.Success);

                string command = $"sudo sed -i 's/^api: no/api: yes/' {configPath} && sudo systemctl restart mediamtx.service";
                await _sshService.ExecuteCommandAsync(command);

                AddLine("✓ MediaMTX Restarted to apply updates.", TerminalLineKind.Success);

                // Start tailing the service log automatically
                await StopStreamIfActive();
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


        [RelayCommand(CanExecute = nameof(CanConfigureUart))]
        private async Task ConfigureUartAndReboot()
        {
            IsConfiguringUart = true;

            try
            {
                AddLine("▶ Checking UART/Bluetooth configuration on target device...", TerminalLineKind.Info);

                bool uartConfigured = await _sshService.ConfigureStableUartAsync(password: Password);

                if (!uartConfigured)
                {
                    AddLine("· UART0 (ttyAMA0) already configured, no changes needed.", TerminalLineKind.Info);
                    return;
                }

                AddLine("✓ UART0 (ttyAMA0) configured — Bluetooth disabled successfully.", TerminalLineKind.Success);
                AddLine("▶ Rebooting device to apply UART configuration...", TerminalLineKind.Info);

                MainThread.BeginInvokeOnMainThread(() =>
                    UartConfigStatus = "Rebooting device, please wait...");

                await StopStreamIfActive();

                await _sshService.RebootAndWaitAsync(
                    password: Password,
                    waitSeconds: 30);

                AddLine("✓ Device is back online. UART0 is now active on GPIO 14/15.", TerminalLineKind.Success);

                MainThread.BeginInvokeOnMainThread(() =>
                    UartConfigStatus = "UART configured successfully.");
            }
            catch (TimeoutException tex)
            {
                _logger.LogError(tex, "Device did not come back online after reboot.");
                AddLine($"✗ Device did not respond after reboot: {tex.Message}", TerminalLineKind.Error);
                UartConfigStatus = "Reboot timed out — check device manually.";
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "UART configuration failed.");
                AddLine($"✗ UART configuration failed: {ex.Message}", TerminalLineKind.Error);
                UartConfigStatus = "Configuration aborted due to errors.";
            }
            finally
            {
                IsConfiguringUart = false;
            }
        }

        private bool CanConfigureUart() => !IsConfiguringUart && !string.IsNullOrWhiteSpace(Password);

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

        // ── FFmpeg deployment ─────────────────────────────────────────────────

        [RelayCommand(CanExecute = nameof(CanDeployFfmpeg))]
        private async Task DeployFfmpeg()
        {
            IsFfmpegDeploying = true;
            FfmpegDeployProgress = 0;
            FfmpegDeployStatus = string.Empty;

            var progress = new Progress<(string Step, double Percent)>(report =>
            {
                MainThread.BeginInvokeOnMainThread(() =>
                {
                    FfmpegDeployProgress = report.Percent;
                    FfmpegDeployStatus = $"{report.Step} ({report.Percent:F0}%)";
                    AddLine(report.Step, TerminalLineKind.Info);
                });
            });

            try
            {
                AddLine($"▶ Deploying FFmpeg from: {Path.GetFileName(FfmpegLocalPath)}", TerminalLineKind.Info);

                await _sshService.DeployFfmpegAsync(
                    localTarGzPath: FfmpegLocalPath,
                    remoteInstallDir: FfmpegInstallDir,
                    serviceUser: FfmpegServiceUser,
                    progress: progress);

                AddLine("✓ FFmpeg deployed and available on PATH.", TerminalLineKind.Success);

                // Verify the install by printing the version
                AddLine("▶ Verifying FFmpeg install…", TerminalLineKind.Info);
                var result = await _sshService.ExecuteCommandAsync("ffmpeg -version", CancellationToken.None);
                foreach (var line in result.StdOut.Split('\n', StringSplitOptions.RemoveEmptyEntries))
                    AddLine(line, TerminalLineKind.Output);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "FFmpeg deployment failed");
                AddLine($"✗ FFmpeg deployment failed: {ex.Message}", TerminalLineKind.Error);
                FfmpegDeployStatus = "Deployment failed";
            }
            finally
            {
                IsFfmpegDeploying = false;
            }
        }

        private bool CanDeployFfmpeg() =>
            IsConnected &&
            !IsFfmpegDeploying &&
            !string.IsNullOrWhiteSpace(FfmpegLocalPath) &&
            File.Exists(FfmpegLocalPath);

        [RelayCommand]
        private async Task PickFfmpegFile()
        {
            try
            {
                var result = await FilePicker.Default.PickAsync(new PickOptions
                {
                    PickerTitle = "Select FFmpeg .tar.gz / .tar.xz archive",
                    FileTypes = new FilePickerFileType(new Dictionary<DevicePlatform, IEnumerable<string>>
                    {
                        { DevicePlatform.WinUI,       new[] { ".gz", ".tgz", ".xz" } },
                        { DevicePlatform.Android,     new[] { "application/gzip", "application/x-gzip", "application/x-xz", "application/x-tar" } },
                        { DevicePlatform.iOS,         new[] { "public.tar-archive", "org.gnu.gnu-zip-archive" } },
                        { DevicePlatform.MacCatalyst, new[] { "public.tar-archive", "org.gnu.gnu-zip-archive" } },
                    })
                });

                if (result is not null)
                {
                    FfmpegLocalPath = result.FullPath;
                    AddLine($"Selected: {result.FileName}", TerminalLineKind.Info);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "File picker error");
                AddLine($"File picker error: {ex.Message}", TerminalLineKind.Error);
            }
        }

        // ── .NET App deployment ───────────────────────────────────────────────

        [RelayCommand(CanExecute = nameof(CanDeployDotNetApp))]
        private async Task DeployDotNetApp()
        {
            IsDotNetDeploying = true;
            DotNetDeployProgress = 0;
            DotNetDeployStatus = string.Empty;

            var progress = new Progress<(string File, double OverallPercent)>(report =>
            {
                MainThread.BeginInvokeOnMainThread(() =>
                {
                    DotNetDeployProgress = report.OverallPercent;
                    DotNetDeployStatus = $"Uploading: {report.File} ({report.OverallPercent:F0}%)";
                    AddLine($"Syncing: {report.File}", TerminalLineKind.Info);
                });
            });

            try
            {
                AddLine($"▶ Starting pipeline sync deployment from folder: {DotNetLocalPath}", TerminalLineKind.Info);

                await _sshService.DeployDotNetAppAsync(
                    localFolder: DotNetLocalPath,
                    remoteFolder: DotNetInstallDir,
                    executableName: DotNetExecutableName,
                    password: Password,
                    progress: progress);

                MainThread.BeginInvokeOnMainThread(() =>
                {
                    DotNetDeployStatus = "Registering background system service...";
                });

                await _sshService.InstallDotNetServiceAsync(
                    remoteFolder: DotNetInstallDir,
                    executableName: DotNetExecutableName,
                    serviceName: DotNetServiceName,
                    serviceUser: UserName,
                    password: Password);

                AddLine($"✓ .NET App '{DotNetServiceName}' successfully registered and activated on boot.", TerminalLineKind.Success);

                await StopStreamIfActive();
                AddLine($"▶ Tracking systemd runtime feedback messages for {DotNetServiceName}...", TerminalLineKind.Info);
                IsStreaming = true;
                _activeStream = _sshService.StartStreamingCommand($"sudo journalctl -u {DotNetServiceName}.service -f -n 30 --no-pager");
                _activeStream.LineReceived += OnStreamLine;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, ".NET deployment process exception caught.");
                AddLine($"✗ Application installation deployment failed: {ex.Message}", TerminalLineKind.Error);
                DotNetDeployStatus = "Deployment aborted due to configuration errors.";
            }
            finally
            {
                IsDotNetDeploying = false;
            }
        }

        private bool CanDeployDotNetApp() =>
            IsConnected &&
            !IsDotNetDeploying &&
            !string.IsNullOrWhiteSpace(DotNetLocalPath) &&
            Directory.Exists(DotNetLocalPath);

        [RelayCommand]
        private async Task PickDotNetFolder()
        {
            try
            {
                var result = await FolderPicker.Default.PickAsync(CancellationToken.None);

                if (result.IsSuccessful && result.Folder is not null)
                {
                    DotNetLocalPath = result.Folder.Path;
                    AddLine($"Target deployment path selected: {result.Folder.Name}", TerminalLineKind.Info);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Folder extraction lookup pointer exception.");
                AddLine($"Folder selection failed: {ex.Message}", TerminalLineKind.Error);
            }
        }

        // ── Navigation ────────────────────────────────────────────────────────

        public void OnAppearing()
        {
            _disposed = false;
            Host = _sqliteService.config.IPAddress;
        }

        [RelayCommand]
        private async Task GoBack()
        {

            await StopStreamIfActive();
            await Shell.Current.GoToAsync("..");
        }

        // ── Private helpers ───────────────────────────────────────────────────

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
            if (_disposed)
                return;

            // Marshal to the UI thread
            MainThread.BeginInvokeOnMainThread(() =>
            {
                if (_disposed)
                    return;

                AddLine(line, TerminalLineKind.Stream);
            });
        }

        /// <summary>
        /// Clears the terminal buffer. Mirrors LogsViewModel.ClearTail() — kept
        /// as its own helper so both the Clear button and Dispose() can reuse it
        /// without duplicating the queue/property reset logic.
        /// </summary>
        private void ClearTerminalOutput()
        {
            _terminalLines.Clear();
            TerminalOutput = string.Empty;
        }

        /// <summary>
        /// Best-effort teardown for when the page is dismissed by something
        /// other than the Back button (Shell popping it, OS swipe-back, etc.).
        /// Unsubscribes from the active stream so a stray callback can't touch
        /// a dead view model, and clears the terminal buffer so a large
        /// accumulated buffer doesn't cost layout time if the view model is
        /// ever reused. Safe to call more than once.
        /// </summary>
        public void Dispose()
        {
            if (_disposed)
                return;

            _disposed = true;

            if (_activeStream is not null)
            {
                var stream = _activeStream;
                _activeStream = null;

                stream.LineReceived -= OnStreamLine;

                try
                {
                    _ = stream.DisposeAsync().AsTask();
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "Error disposing active stream (best-effort).");
                }
            }

            IsStreaming = false;
            ClearTerminalOutput();
        }

        private void AddLine(string text, TerminalLineKind kind = TerminalLineKind.Output)
        {
            _terminalLines.Enqueue(FormatLine(text, kind));

            while (_terminalLines.Count > MaxTerminalLines)
                _terminalLines.Dequeue();

            TerminalOutput = string.Join(Environment.NewLine, _terminalLines);
        }

        private static string FormatLine(string text, TerminalLineKind kind) => kind switch
        {
            TerminalLineKind.Error => $"✗ {text}",
            TerminalLineKind.Info => $"· {text}",
            TerminalLineKind.Success => $"✓ {text}",
            TerminalLineKind.Command => $"$ {text}",
            TerminalLineKind.Stream => text,
            _ => text,
        };
    }
}