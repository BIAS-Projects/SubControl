using Microsoft.Extensions.Logging;
using Renci.SshNet;
using Renci.SshNet.Common;
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace SubControlMAUI.Services
{
    /// <summary>
    /// Result of a single SSH command execution.
    /// </summary>
    public record SshCommandResult(
        string Command,
        string StdOut,
        string StdErr,
        int ExitCode,
        TimeSpan Duration
    )
    {
        public bool Succeeded => ExitCode == 0;
    }

    /// <summary>
    /// Represents a live streaming session (e.g. tailing logs or running a long process).
    /// Dispose to cancel and close the channel.
    /// </summary>
    public sealed class SshStreamSession : IAsyncDisposable
    {
        private readonly ShellStream _shell;
        private readonly CancellationTokenSource _cts;
        private readonly Task _readerTask;

        public event Action<string>? LineReceived;

        internal SshStreamSession(ShellStream shell, CancellationTokenSource cts, Task readerTask)
        {
            _shell = shell;
            _cts = cts;
            _readerTask = readerTask;
        }

        internal void RaiseLine(string line) => LineReceived?.Invoke(line);

        public async ValueTask DisposeAsync()
        {
            _cts.Cancel();
            try { await _readerTask.WaitAsync(TimeSpan.FromSeconds(3)); } catch { /* best-effort */ }
            _shell.Dispose();
            _cts.Dispose();
        }
    }

    /// <summary>
    /// Manages an SSH connection to a Raspberry Pi (or any Linux host).
    /// Supports:
    ///   - Password and private-key authentication
    ///   - One-shot command execution with stdout/stderr capture
    ///   - Streaming shell sessions for log tailing / long-running processes
    ///   - SFTP file upload / download (for future .NET app deployment)
    /// </summary>
    public sealed class SshService : IAsyncDisposable
    {
        // ── Dependencies ────────────────────────────────────────────────────────
        private readonly ILogger<SshService> _logger;

        // ── Connection state ────────────────────────────────────────────────────
        private SshClient? _sshClient;
        private SftpClient? _sftpClient;
        private readonly SemaphoreSlim _connectionLock = new(1, 1);

        // ── Configuration ───────────────────────────────────────────────────────
        public string Host { get; private set; } = string.Empty;
        public string Username { get; private set; } = string.Empty;
        public int Port { get; private set; } = 22;
        public bool IsConnected => _sshClient?.IsConnected == true;

        public SshService(ILogger<SshService> logger)
        {
            _logger = logger;
        }

        // ── Connection ───────────────────────────────────────────────────────────

        /// <summary>
        /// Connect using a password. Call before any other method.
        /// </summary>
        public async Task ConnectAsync(
            string host,
            string username,
            string password,
            int port = 22,
            CancellationToken cancellationToken = default)
        {
            await _connectionLock.WaitAsync(cancellationToken);
            try
            {
                if (IsConnected)
                {
                    _logger.LogWarning("SSH already connected to {Host}. Disconnect first.", Host);
                    return;
                }

                Host = host;
                Username = username;
                Port = port;

                _logger.LogInformation("Connecting to {Username}@{Host}:{Port} (password auth)…", username, host, port);

                var authMethod = new PasswordAuthenticationMethod(username, password);
                var connectionInfo = new ConnectionInfo(host, port, username, authMethod)
                {
                    Timeout = TimeSpan.FromSeconds(15)
                };

                _sshClient = new SshClient(connectionInfo);
                _sftpClient = new SftpClient(connectionInfo);

                await Task.Run(() => _sshClient.Connect(), cancellationToken);
                await Task.Run(() => _sftpClient.Connect(), cancellationToken);

                _logger.LogInformation("SSH connected to {Host} (server version: {Version})",
                    host, _sshClient.ConnectionInfo.ServerVersion);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to connect to {Username}@{Host}:{Port}", username, host, port);
                DisposeClients();
                throw;
            }
            finally
            {
                _connectionLock.Release();
            }
        }

        /// <summary>
        /// Connect using a private key file (e.g. ~/.ssh/id_rsa).
        /// </summary>
        public async Task ConnectWithKeyAsync(
            string host,
            string username,
            string privateKeyPath,
            string? passphrase = null,
            int port = 22,
            CancellationToken cancellationToken = default)
        {
            await _connectionLock.WaitAsync(cancellationToken);
            try
            {
                if (IsConnected)
                {
                    _logger.LogWarning("SSH already connected to {Host}. Disconnect first.", Host);
                    return;
                }

                Host = host;
                Username = username;
                Port = port;

                _logger.LogInformation("Connecting to {Username}@{Host}:{Port} (key auth: {KeyPath})…",
                    username, host, port, privateKeyPath);

                var keyFile = passphrase is null
                    ? new PrivateKeyFile(privateKeyPath)
                    : new PrivateKeyFile(privateKeyPath, passphrase);

                var authMethod = new PrivateKeyAuthenticationMethod(username, keyFile);
                var connectionInfo = new ConnectionInfo(host, port, username, authMethod)
                {
                    Timeout = TimeSpan.FromSeconds(15)
                };

                _sshClient = new SshClient(connectionInfo);
                _sftpClient = new SftpClient(connectionInfo);

                await Task.Run(() => _sshClient.Connect(), cancellationToken);
                await Task.Run(() => _sftpClient.Connect(), cancellationToken);

                _logger.LogInformation("SSH connected to {Host}", host);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to connect to {Username}@{Port} with key", username, port);
                DisposeClients();
                throw;
            }
            finally
            {
                _connectionLock.Release();
            }
        }

        /// <summary>Gracefully disconnect both SSH and SFTP clients.</summary>
        public async Task DisconnectAsync()
        {
            await _connectionLock.WaitAsync();
            try
            {
                _logger.LogInformation("Disconnecting from {Host}…", Host);
                await Task.Run(() =>
                {
                    _sshClient?.Disconnect();
                    _sftpClient?.Disconnect();
                });
                DisposeClients();
                _logger.LogInformation("Disconnected.");
            }
            finally
            {
                _connectionLock.Release();
            }
        }

        // ── Command Execution ────────────────────────────────────────────────────

        /// <summary>
        /// Run a command and return its stdout, stderr, and exit code.
        /// Throws if not connected.
        /// </summary>
        public async Task<SshCommandResult> ExecuteCommandAsync(
            string command,
            CancellationToken cancellationToken = default)
        {
            EnsureConnected();

            _logger.LogDebug("Executing command: {Command}", command);
            var started = DateTime.UtcNow;

            using var sshCommand = _sshClient!.CreateCommand(command);
            sshCommand.CommandTimeout = TimeSpan.FromMinutes(5);

            try
            {
                var stdout = await Task.Run(() => sshCommand.Execute(), cancellationToken);
                var stderr = sshCommand.Error;
                var exitCode = sshCommand.ExitStatus ?? -1;
                var duration = DateTime.UtcNow - started;

                if (exitCode == 0)
                {
                    _logger.LogInformation(
                        "Command succeeded in {Duration:N0}ms: {Command}",
                        duration.TotalMilliseconds, command);
                }
                else
                {
                    _logger.LogWarning(
                        "Command exited {ExitCode} in {Duration:N0}ms: {Command} | stderr: {StdErr}",
                        exitCode, duration.TotalMilliseconds, command, stderr?.Trim());
                }

                return new SshCommandResult(command, stdout ?? string.Empty, stderr ?? string.Empty, exitCode, duration);
            }
            catch (OperationCanceledException)
            {
                _logger.LogWarning("Command cancelled: {Command}", command);
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Command threw an exception: {Command}", command);
                throw;
            }
        }

        /// <summary>
        /// Run multiple commands in sequence. Stops on first failure unless
        /// <paramref name="continueOnError"/> is true.
        /// </summary>
        public async Task<IReadOnlyList<SshCommandResult>> ExecuteCommandsAsync(
            IEnumerable<string> commands,
            bool continueOnError = false,
            CancellationToken cancellationToken = default)
        {
            var results = new List<SshCommandResult>();
            foreach (var cmd in commands)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var result = await ExecuteCommandAsync(cmd, cancellationToken);
                results.Add(result);

                if (!result.Succeeded && !continueOnError)
                {
                    _logger.LogWarning("Aborting command sequence after failure: {Command}", cmd);
                    break;
                }
            }
            return results;
        }

        // ── Streaming Shell ──────────────────────────────────────────────────────

        /// <summary>
        /// Open a pseudo-terminal shell stream and start an initial command (e.g. "tail -f app.log").
        /// Subscribe to <see cref="SshStreamSession.LineReceived"/> to receive output lines.
        /// Dispose the returned session to terminate.
        /// </summary>
        public SshStreamSession StartStreamingCommand(string command)
        {
            EnsureConnected();

            _logger.LogInformation("Starting streaming command: {Command}", command);

            var cts = new CancellationTokenSource();
            var shell = _sshClient!.CreateShellStream("xterm", 200, 50, 800, 600, 4096);

            // Use an Action so the reader task doesn't need to capture 'session'
            // before it is assigned. The session wires itself in after construction.
            Action<string>? raiseLine = null;

            var readerTask = Task.Run(async () =>
            {
                var buffer = new StringBuilder();
                try
                {
                    // Small delay to let the shell stream initialise
                    await Task.Delay(100, cts.Token);

                    shell.WriteLine(command);

                    using var reader = new StreamReader(shell);
                    var charBuf = new char[256];

                    while (!cts.Token.IsCancellationRequested)
                    {
                        if (shell.DataAvailable)
                        {
                            int read = await reader.ReadAsync(charBuf, 0, charBuf.Length);
                            if (read == 0) break;

                            buffer.Append(charBuf, 0, read);

                            // Flush complete lines
                            var text = buffer.ToString();
                            var newlineIdx = text.LastIndexOf('\n');
                            if (newlineIdx >= 0)
                            {
                                var lines = text[..(newlineIdx + 1)].Split('\n', StringSplitOptions.RemoveEmptyEntries);
                                buffer.Clear();
                                buffer.Append(text[(newlineIdx + 1)..]);

                                foreach (var line in lines)
                                {
                                    var cleaned = line.Replace("\r", "").Trim();
                                    if (!string.IsNullOrEmpty(cleaned))
                                        raiseLine?.Invoke(cleaned);
                                }
                            }
                        }
                        else
                        {
                            await Task.Delay(50, cts.Token);
                        }
                    }
                }
                catch (OperationCanceledException) { /* expected on dispose */ }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Streaming reader faulted for command: {Command}", command);
                }
            }, cts.Token);

            var session = new SshStreamSession(shell, cts, readerTask);

            // Wire the delegate now that session exists
            raiseLine = session.RaiseLine;

            return session;
        }

        // ── File Transfer (SFTP) ─────────────────────────────────────────────────

        /// <summary>
        /// Upload a local file to the remote host over SFTP.
        /// </summary>
        public async Task UploadFileAsync(
            string localPath,
            string remotePath,
            IProgress<double>? progress = null,
            CancellationToken cancellationToken = default)
        {
            EnsureConnected();

            if (!File.Exists(localPath))
                throw new FileNotFoundException("Local file not found.", localPath);

            _logger.LogInformation("Uploading {LocalPath} → {RemotePath}", localPath, remotePath);

            await Task.Run(() =>
            {
                using var fs = File.OpenRead(localPath);
                var totalBytes = fs.Length;
                long uploaded = 0;

                _sftpClient!.UploadFile(fs, remotePath, uploaded =>
                {
                    Interlocked.Add(ref uploaded, 0); // suppress CS0728
                    progress?.Report((double)uploaded / totalBytes * 100.0);
                });

                _logger.LogInformation("Upload complete: {RemotePath} ({Bytes:N0} bytes)", remotePath, totalBytes);
            }, cancellationToken);
        }

        /// <summary>
        /// Download a remote file to local disk.
        /// </summary>
        public async Task DownloadFileAsync(
            string remotePath,
            string localPath,
            CancellationToken cancellationToken = default)
        {
            EnsureConnected();

            _logger.LogInformation("Downloading {RemotePath} → {LocalPath}", remotePath, localPath);

            await Task.Run(() =>
            {
                using var fs = File.OpenWrite(localPath);
                _sftpClient!.DownloadFile(remotePath, fs);
                _logger.LogInformation("Download complete: {LocalPath}", localPath);
            }, cancellationToken);
        }

        /// <summary>
        /// Deploy a .NET console application folder to the Pi using sudo elevation.
        /// </summary>
        public async Task DeployDotNetAppAsync(
            string localFolder,
            string remoteFolder,
            string executableName,
            string password = "1234",
            IProgress<(string File, double OverallPercent)>? progress = null,
            CancellationToken cancellationToken = default)
        {
            EnsureConnected();

            _logger.LogInformation("Deploying .NET app from {Local} to {Remote}:{Folder}",
                localFolder, Host, remoteFolder);

            var escapedPwd = password.Replace("'", "'\\''");
            var targetFolder = remoteFolder.TrimEnd('/');

            await ExecuteCommandAsync($"printf '{escapedPwd}\\n' | sudo -S mkdir -p \"{targetFolder}\"", cancellationToken);

            var verifyDir = await ExecuteCommandAsync($"test -d {targetFolder} && echo 'yes' || echo 'no'", cancellationToken);

            if (verifyDir.StdOut.Trim() == "yes")
            {
                progress?.Report(($"Directory {targetFolder} was created successfully.", 35));
                _logger.LogInformation("Directory: {Path} created", targetFolder);
            }
            else
            {
                progress?.Report(($"{targetFolder} creation failed.", 35));
                _logger.LogError("Directory creation failed.");
            }

            var files = Directory.GetFiles(localFolder, "*", SearchOption.AllDirectories);

            string destinationFolder = "";

            for (int i = 0; i < files.Length; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var localFile = files[i];
                var relativePath = Path.GetRelativePath(localFolder, localFile).Replace('\\', '/');

                var finalRemotePath = $"{targetFolder}/{relativePath}";
                var tmpStagingPath = $"/tmp/dotnet_deploy_{Guid.NewGuid():N}";
                destinationFolder = finalRemotePath;

                var finalRemoteDir = Path.GetDirectoryName(finalRemotePath)?.Replace('\\', '/');
                if (!string.IsNullOrEmpty(finalRemoteDir))
                {
                    await ExecuteCommandAsync($"printf '{escapedPwd}\\n' | sudo -S mkdir -p \"{finalRemoteDir}\"", cancellationToken);
                }

                progress?.Report((relativePath, (double)i / files.Length * 100.0));

                await UploadFileAsync(localFile, tmpStagingPath, cancellationToken: cancellationToken);
                await ExecuteCommandAsync($"printf '{escapedPwd}\\n' | sudo -S mv \"{tmpStagingPath}\" \"{finalRemotePath}\"", cancellationToken);
            }

            var contents = await ExecuteCommandAsync($"ls -laR \"{targetFolder}\"", cancellationToken);
            _logger.LogInformation("Current files in {installDir}:\n{Files}", targetFolder, contents.StdOut);
            progress?.Report(($"Current files in {targetFolder} {contents.StdOut}", 100));

            await ExecuteCommandAsync($"printf '{escapedPwd}\\n' | sudo -S chown -R {Username}:{Username} \"{remoteFolder}\"", cancellationToken);
            await ExecuteCommandAsync($"printf '{escapedPwd}\\n' | sudo -S chmod -R 755 \"{remoteFolder}\"", cancellationToken);
            await ExecuteCommandAsync($"printf '{escapedPwd}\\n' | sudo -S chmod +x \"{remoteFolder}\"/{executableName}", cancellationToken);

            contents = await ExecuteCommandAsync($"ls -la \"{remoteFolder}\"", cancellationToken);
            _logger.LogInformation("Current files in {installDir}:\n{Files}", remoteFolder, contents.StdOut);
            progress?.Report(($"Current files in {remoteFolder} {contents.StdOut}", 100));

            var exePath = $"{targetFolder}/{executableName}";
            await ExecuteCommandAsync($"printf '{escapedPwd}\\n' | sudo -S chmod +x \"{exePath}\"", cancellationToken);

            _logger.LogInformation("Deployment complete. Entry point: {ExePath}", exePath);
            progress?.Report((executableName, 100.0));
        }

        /// <summary>
        /// Installs and configures the deployed .NET console application as a systemd service.
        /// </summary>
        public async Task InstallDotNetServiceAsync(
            string remoteFolder,
            string executableName,
            string serviceName = "mynetapp",
            string serviceUser = "pi",
            string password = "1234",
            CancellationToken cancellationToken = default)
        {
            EnsureConnected();

            var targetFolder = remoteFolder.TrimEnd('/');
            var binaryPath = $"{targetFolder}/{executableName}";
            var unitPath = $"/etc/systemd/system/{serviceName}.service";
            var escapedPwd = password.Replace("'", "'\\''");

            _logger.LogInformation("Installing .NET background service: {Service} -> {Binary}", serviceName, binaryPath);

            var unitLines = new[]
            {
                "[Unit]",
                $"Description=.NET Application: {serviceName}",
                "After=network.target",
                "",
                "[Service]",
                "Type=notify",
                $"User={serviceUser}",
                $"WorkingDirectory={targetFolder}",
                $"ExecStart={binaryPath}",
                "Restart=on-failure",
                "RestartSec=5",
                "StandardOutput=journal",
                "StandardError=journal",
                "Environment=DOTNET_ENVIRONMENT=Production",
                "",
                "[Install]",
                "WantedBy=multi-user.target"
            };

            var rawUnitContent = string.Join("\n", unitLines) + "\n";
            var base64UnitBytes = System.Text.Encoding.UTF8.GetBytes(rawUnitContent);
            var base64UnitString = Convert.ToBase64String(base64UnitBytes);

            _logger.LogInformation("Writing service unit file to {Path}", unitPath);
            await ExecuteCommandAsync($"printf '{escapedPwd}\\n' | sudo -S sh -c 'echo \"{base64UnitString}\" | base64 -d > {unitPath}'", cancellationToken);

            await ExecuteCommandAsync($"printf '{escapedPwd}\\n' | sudo -S systemctl daemon-reload", cancellationToken);

            _logger.LogInformation("Enabling service on system boot...");
            await ExecuteCommandAsync($"printf '{escapedPwd}\\n' | sudo -S systemctl enable {serviceName}.service", cancellationToken);

            _logger.LogInformation("Starting service...");
            await ExecuteCommandAsync($"printf '{escapedPwd}\\n' | sudo -S systemctl restart {serviceName}.service", cancellationToken);

            await Task.Delay(2000, cancellationToken);
            var status = await ExecuteCommandAsync($"printf '{escapedPwd}\\n' | sudo -S systemctl is-active {serviceName}.service", cancellationToken);

            if (status.StdOut.Trim() == "active")
            {
                _logger.LogInformation(".NET background service '{Service}' is active and running successfully!", serviceName);
            }
            else
            {
                var journal = await ExecuteCommandAsync($"printf '{escapedPwd}\\n' | sudo -S journalctl -u {serviceName}.service -n 20 --no-pager", cancellationToken);
                _logger.LogError("Service failed to start. System Journal Logs:\n{Logs}", journal.StdOut);
                throw new InvalidOperationException($"The .NET background service '{serviceName}' failed to enter an active running state.");
            }
        }

        // ── MediaMTX Deployment ──────────────────────────────────────────────────

        /// <summary>
        /// Full MediaMTX deployment pipeline:
        ///   1. Upload the .tar.gz from the local device to the Pi via SFTP
        ///   2. Extract it into <paramref name="remoteInstallDir"/>
        ///   3. Grant the binary the Linux capabilities it needs
        ///   4. Write a minimal mediamtx.yml if none exists
        ///   5. Install a systemd unit, enable it on boot, and start it immediately
        ///   6. Verify the service reached the active state
        ///
        /// Progress is reported as (StepDescription, 0-100).
        /// </summary>
        public async Task DeployMediaMtxAsync(
            string localTarGzPath,
            string remoteInstallDir = "/opt/mediamtx",
            string serviceUser = "pi",
            string password = "1234",
            IProgress<(string Step, int Percent)>? progress = null,
            CancellationToken cancellationToken = default)
        {
            EnsureConnected();

            if (!File.Exists(localTarGzPath))
                throw new FileNotFoundException("MediaMTX archive not found.", localTarGzPath);

            var fileName = Path.GetFileName(localTarGzPath);
            var remoteTar = "/tmp/" + fileName;
            var installDir = remoteInstallDir.TrimEnd('/');
            var binaryPath = installDir + "/mediamtx";
            var configPath = installDir + "/mediamtx.yml";
            var unitPath = "/etc/systemd/system/mediamtx.service";
            var escapedPwd = password.Replace("'", "'\\''");

            _logger.LogInformation(
                "Starting MediaMTX deployment: {Archive} -> {Host}:{Dir}",
                fileName, Host, remoteInstallDir);

            // ── Step 1: Upload ────────────────────────────────────────────────
            progress?.Report(("Uploading archive...", 5));
            _logger.LogInformation("Step 1/6 - Uploading {File}", fileName);

            await Task.Run(() =>
            {
                using var fs = File.OpenRead(localTarGzPath);
                var totalBytes = fs.Length;

                _sftpClient!.UploadFile(fs, remoteTar, bytesUploaded =>
                {
                    var pct = (int)Math.Min(5 + bytesUploaded * 20.0 / totalBytes, 24);
                    progress?.Report(("Uploading... " + (bytesUploaded / 1024 / 1024) + " MB", pct));
                });
            }, cancellationToken);

            _logger.LogInformation("Upload complete: {RemotePath}", remoteTar);
            progress?.Report(("Upload complete", 25));

            // ── Step 2: Prepare install directory and extract ─────────────────
            progress?.Report(("Preparing install directory...", 30));
            _logger.LogInformation("Step 2/6 - Creating {Dir}", installDir);

            await RunStep($"printf '{escapedPwd}\\n' | sudo -S mkdir -p {installDir} && test -d {installDir} && echo 'SUCCESS'", cancellationToken);

            var verifyDir = await ExecuteCommandAsync($"test -d {installDir} && echo 'yes' || echo 'no'", cancellationToken);

            if (verifyDir.StdOut.Trim() == "yes")
            {
                progress?.Report(($"Directory {installDir} was created successfully.", 35));
                _logger.LogInformation("Directory: {Path} created", installDir);
            }
            else
            {
                progress?.Report(($"{installDir} creation failed.", 35));
                _logger.LogError("Directory creation failed.");
            }

            progress?.Report(("Extracting archive...", 40));
            _logger.LogInformation("Step 3/6 - Extracting {File} into {Dir}", fileName, installDir);

            await RunStep($"printf '{escapedPwd}\\n' | sudo -S tar -xzf {remoteTar} -C {installDir}", cancellationToken);
            await RunStep($"rm -f {remoteTar}", cancellationToken);

            progress?.Report(("Extraction complete", 55));
            _logger.LogInformation("Binary expected at: {Path}", binaryPath);

            var contents = await ExecuteCommandAsync($"ls -la {installDir}", cancellationToken);
            _logger.LogInformation("Current files in {installDir}:\n{Files}", installDir, contents.StdOut);
            progress?.Report(($"Current files in {installDir} {contents.StdOut}", 55));

            // ── Step 3: Permissions and capabilities ──────────────────────────
            progress?.Report(("Setting permissions and capabilities...", 60));
            _logger.LogInformation("Step 4/6 - Granting capabilities to {Binary}", binaryPath);

            await RunStep($"printf '{escapedPwd}\\n' | sudo -S chmod +x {binaryPath}", cancellationToken);
            await RunStep($"printf '{escapedPwd}\\n' | sudo -S chown {serviceUser}:{serviceUser} {binaryPath}", cancellationToken);
            await RunStep($"printf '{escapedPwd}\\n' | sudo -S setcap cap_net_bind_service,cap_net_admin,cap_net_raw=+ep {binaryPath}", cancellationToken);

            progress?.Report(("Capabilities granted", 70));

            // ── Step 4: Write default config if none exists ───────────────────
            var configCheck = await ExecuteCommandAsync($"test -f {configPath} && echo yes || echo no", cancellationToken);

            if (configCheck.StdOut.Trim() == "no")
            {
                _logger.LogInformation("Step 5/6 - Writing default mediamtx.yml");

                var configLines = new[]
                {
                    "logLevel: info",
                    "logDestinations: [stdout]",
                    "rtsp: yes",
                    "rtspAddress: :8554",
                    "rtmp: yes",
                    "rtmpAddress: :1935",
                    "hls: yes",
                    "hlsAddress: :8888",
                    "webrtc: yes",
                    "webrtcAddress: :8889",
                    "paths:",
                    "  all:",
                    "    source: publisher"
                };

                var rawConfigContent = string.Join("\n", configLines) + "\n";
                var base64ConfigBytes = System.Text.Encoding.UTF8.GetBytes(rawConfigContent);
                var base64ConfigString = Convert.ToBase64String(base64ConfigBytes);

                await RunStep($"printf '{escapedPwd}\\n' | sudo -S sh -c 'echo \"{base64ConfigString}\" | base64 -d > {configPath}'", cancellationToken);

                var checkConfigFile = await ExecuteCommandAsync($"ls -la {configPath}", cancellationToken);
                _logger.LogInformation("Configuration file verification:\n{Result}", checkConfigFile.StdOut);
            }
            else
            {
                _logger.LogInformation("Step 5/6 - Existing config retained at {Path}", configPath);
            }

            progress?.Report(("Configuration ready", 75));

            // ── Step 5: Install systemd unit ──────────────────────────────────
            progress?.Report(("Installing systemd service...", 80));
            _logger.LogInformation("Step 6/6 - Writing systemd unit to {Path}", unitPath);

            var unitLines = new[]
            {
                "[Unit]",
                "Description=MediaMTX RTSP/RTMP/HLS/WebRTC media server",
                "After=network.target",
                "",
                "[Service]",
                "User=" + serviceUser,
                "AmbientCapabilities=CAP_NET_BIND_SERVICE CAP_NET_ADMIN CAP_NET_RAW",
                "CapabilityBoundingSet=CAP_NET_BIND_SERVICE CAP_NET_ADMIN CAP_NET_RAW",
                "ExecStart=" + binaryPath + " " + configPath,
                "Restart=on-failure",
                "RestartSec=5",
                "StandardOutput=journal",
                "StandardError=journal",
                "",
                "[Install]",
                "WantedBy=multi-user.target"
            };

            var rawUnitContent = string.Join("\n", unitLines) + "\n";
            var base64UnitBytes = Encoding.UTF8.GetBytes(rawUnitContent);
            var base64UnitString = Convert.ToBase64String(base64UnitBytes);

            await RunStep($"printf '{escapedPwd}\\n' | sudo -S sh -c 'echo \"{base64UnitString}\" | base64 -d > {unitPath}'", cancellationToken);
            await RunStep($"printf '{escapedPwd}\\n' | sudo -S systemctl daemon-reload", cancellationToken);

            var checkUnitFile = await ExecuteCommandAsync($"ls -la {unitPath}", cancellationToken);
            progress?.Report(($"Systemd unit file check:\n{checkUnitFile.StdOut}", 80));
            _logger.LogInformation("Systemd unit file check:\n{Result}", checkUnitFile.StdOut);

            await RunStep($"printf '{escapedPwd}\\n' | sudo -S systemctl enable mediamtx.service", cancellationToken);

            // ── Step 6: Start and verify ──────────────────────────────────────
            progress?.Report(("Starting MediaMTX service...", 90));
            await RunStep($"printf '{escapedPwd}\\n' | sudo -S systemctl restart mediamtx.service", cancellationToken);

            await Task.Delay(2000, cancellationToken);

            var status = await ExecuteCommandAsync($"printf '{escapedPwd}\\n' | sudo -S systemctl is-active mediamtx.service", cancellationToken);
            var isActive = status.StdOut.Trim() == "active";

            if (isActive)
            {
                _logger.LogInformation("MediaMTX service is active and running");
                progress?.Report(("MediaMTX deployed and running", 100));
            }
            else
            {
                var journal = await ExecuteCommandAsync($"printf '{escapedPwd}\\n' | sudo -S journalctl -u mediamtx.service -n 30 --no-pager", cancellationToken);

                _logger.LogWarning(
                    "MediaMTX service did not reach active state. Journal:\n{Journal}",
                    journal.StdOut);

                progress?.Report(("Deployed but service not active - check terminal", 100));

                throw new InvalidOperationException(
                    "MediaMTX service failed to start.\n" + journal.StdOut.Trim());
            }
        }

        // ── FFmpeg Deployment ────────────────────────────────────────────────────

        /// <summary>
        /// Full FFmpeg deployment pipeline for a Raspberry Pi (arm64/armhf):
        ///
        ///   1. Upload the static build archive (.tar.gz or .tar.xz) via SFTP to /tmp
        ///   2. Detect the compression type and extract into a staging directory
        ///   3. Locate the ffmpeg / ffprobe binaries inside the extracted tree
        ///      (handles both flat archives and versioned subdirectory layouts)
        ///   4. Copy the binaries to <paramref name="remoteInstallDir"/> (default /usr/local/bin)
        ///      using sudo so they land on PATH for all users and services
        ///   5. Mark them executable and verify the install with `ffmpeg -version`
        ///
        /// Compatible with archives from:
        ///   - https://johnvansickle.com/ffmpeg/  (ffmpeg-release-arm64-static.tar.xz)
        ///   - https://github.com/BtbN/FFmpeg-Builds/releases  (ffmpeg-*-linux-arm64-*.tar.gz)
        ///   - Any static build that contains an `ffmpeg` binary somewhere in the tree
        ///
        /// Progress is reported as (StepDescription, 0.0-100.0).
        /// </summary>
        public async Task DeployFfmpegAsync(
            string localTarGzPath,
            string remoteInstallDir = "/usr/local/bin",
            string serviceUser = "root",
            string password = "1234",
            IProgress<(string Step, double Percent)>? progress = null,
            CancellationToken cancellationToken = default)
        {
            EnsureConnected();

            if (!File.Exists(localTarGzPath))
                throw new FileNotFoundException("FFmpeg archive not found.", localTarGzPath);

            var fileName = Path.GetFileName(localTarGzPath);
            var remoteTar = "/tmp/" + fileName;
            var stagingDir = "/tmp/ffmpeg_deploy_staging";
            var installDir = remoteInstallDir.TrimEnd('/');
            var escapedPwd = password.Replace("'", "'\\''");

            // Detect whether we need -xzf (gzip) or -xJf (xz) based on extension.
            // .tar.xz uses the J flag; .tar.gz / .tgz use z.
            var isXz = fileName.EndsWith(".tar.xz", StringComparison.OrdinalIgnoreCase) ||
                       fileName.EndsWith(".xz", StringComparison.OrdinalIgnoreCase);
            var tarFlags = isXz ? "-xJf" : "-xzf";

            _logger.LogInformation(
                "Starting FFmpeg deployment: {Archive} ({Compression}) -> {Host}:{Dir}",
                fileName, isXz ? "xz" : "gzip", Host, installDir);

            // ── Step 1: Upload ────────────────────────────────────────────────
            progress?.Report(("Uploading archive...", 5));
            _logger.LogInformation("Step 1/5 - Uploading {File}", fileName);

            await Task.Run(() =>
            {
                using var fs = File.OpenRead(localTarGzPath);
                var totalBytes = fs.Length;

                _sftpClient!.UploadFile(fs, remoteTar, bytesUploaded =>
                {
                    var pct = Math.Min(5 + bytesUploaded * 20.0 / totalBytes, 24);
                    progress?.Report(($"Uploading... {bytesUploaded / 1024 / 1024} MB", pct));
                });
            }, cancellationToken);

            _logger.LogInformation("Upload complete: {RemotePath}", remoteTar);
            progress?.Report(("Upload complete", 25));

            // ── Step 2: Clean staging dir and extract ─────────────────────────
            progress?.Report(("Preparing staging directory...", 28));
            _logger.LogInformation("Step 2/5 - Extracting into {Dir}", stagingDir);

            // Remove any leftovers from a previous attempt then recreate clean
            await RunStep($"rm -rf {stagingDir} && mkdir -p {stagingDir}", cancellationToken);

            progress?.Report(("Extracting archive (this may take a moment)...", 30));

            // xz-compressed static builds can be large; give the command up to 10 minutes
            using var extractCmd = _sshClient!.CreateCommand($"tar {tarFlags} {remoteTar} -C {stagingDir}");
            extractCmd.CommandTimeout = TimeSpan.FromMinutes(10);
            await Task.Run(() => extractCmd.Execute(), cancellationToken);

            if ((extractCmd.ExitStatus ?? -1) != 0)
            {
                throw new InvalidOperationException(
                    $"FFmpeg archive extraction failed (exit {extractCmd.ExitStatus}):\n{extractCmd.Error}");
            }

            // Tidy up the uploaded archive immediately to recover /tmp space
            await ExecuteCommandAsync($"rm -f {remoteTar}", cancellationToken);

            progress?.Report(("Extraction complete", 50));

            // Log staging contents to help diagnose unexpected archive layouts
            var stagingContents = await ExecuteCommandAsync($"find {stagingDir} -maxdepth 3 | head -40", cancellationToken);
            _logger.LogInformation("Staging directory contents:\n{Contents}", stagingContents.StdOut);
            progress?.Report(($"Archive extracted:\n{stagingContents.StdOut}", 52));

            // ── Step 3: Locate the ffmpeg binary inside the extracted tree ────
            //
            // Static build archives use several different layouts:
            //   • johnvansickle: ffmpeg-N-arm64-static/ffmpeg  (binary in a version subdirectory)
            //   • BtbN nightly:  ffmpeg-master-arm64-static/bin/ffmpeg
            //   • Some flat builds: ffmpeg  (binary at the root of the staging dir)
            //
            // `find` with -name ffmpeg -type f handles all of these without hard-coding paths.
            progress?.Report(("Locating ffmpeg binary in archive...", 55));
            _logger.LogInformation("Step 3/5 - Locating ffmpeg binary");

            var findFfmpeg = await ExecuteCommandAsync($"find {stagingDir} -type f -name 'ffmpeg' | head -1", cancellationToken);
            var ffmpegSrcPath = findFfmpeg.StdOut.Trim();

            if (string.IsNullOrEmpty(ffmpegSrcPath))
            {
                throw new InvalidOperationException(
                    "Could not locate an 'ffmpeg' binary inside the extracted archive. " +
                    "Ensure the archive is a static FFmpeg build for arm64/armhf.\n" +
                    $"Archive contents:\n{stagingContents.StdOut}");
            }

            _logger.LogInformation("Found ffmpeg at: {Path}", ffmpegSrcPath);

            // Also look for ffprobe — it ships alongside ffmpeg in most static builds
            var findFfprobe = await ExecuteCommandAsync($"find {stagingDir} -type f -name 'ffprobe' | head -1", cancellationToken);
            var ffprobeSrcPath = findFfprobe.StdOut.Trim();

            if (!string.IsNullOrEmpty(ffprobeSrcPath))
                _logger.LogInformation("Found ffprobe at: {Path}", ffprobeSrcPath);

            progress?.Report(("Binaries located", 60));

            // ── Step 4: Install binaries to the target directory ──────────────
            progress?.Report(($"Installing ffmpeg to {installDir}...", 65));
            _logger.LogInformation("Step 4/5 - Copying binaries to {Dir}", installDir);

            // Ensure the target directory exists (it always should for /usr/local/bin, but be safe)
            await RunStep($"printf '{escapedPwd}\\n' | sudo -S mkdir -p {installDir}", cancellationToken);

            // Copy ffmpeg
            await RunStep(
                $"printf '{escapedPwd}\\n' | sudo -S cp \"{ffmpegSrcPath}\" \"{installDir}/ffmpeg\"",
                cancellationToken);

            await RunStep(
                $"printf '{escapedPwd}\\n' | sudo -S chmod +x \"{installDir}/ffmpeg\"",
                cancellationToken);

            // Copy ffprobe if it was found
            if (!string.IsNullOrEmpty(ffprobeSrcPath))
            {
                await RunStep(
                    $"printf '{escapedPwd}\\n' | sudo -S cp \"{ffprobeSrcPath}\" \"{installDir}/ffprobe\"",
                    cancellationToken);

                await RunStep(
                    $"printf '{escapedPwd}\\n' | sudo -S chmod +x \"{installDir}/ffprobe\"",
                    cancellationToken);

                _logger.LogInformation("ffprobe installed to {Dir}/ffprobe", installDir);
            }

            // Set ownership on the installed binaries
            var chownTargets = string.IsNullOrEmpty(ffprobeSrcPath)
                ? $"\"{installDir}/ffmpeg\""
                : $"\"{installDir}/ffmpeg\" \"{installDir}/ffprobe\"";

            await RunStep(
                $"printf '{escapedPwd}\\n' | sudo -S chown root:root {chownTargets}",
                cancellationToken);

            // Clean up the staging directory
            await ExecuteCommandAsync($"rm -rf {stagingDir}", cancellationToken);

            progress?.Report(("Binaries installed", 85));

            // ── Step 5: Verify ────────────────────────────────────────────────
            progress?.Report(("Verifying FFmpeg install...", 90));
            _logger.LogInformation("Step 5/5 - Verifying installation");

            // Use the full path in case /usr/local/bin isn't on the non-login shell PATH
            var verify = await ExecuteCommandAsync($"\"{installDir}/ffmpeg\" -version 2>&1 | head -3", cancellationToken);

            if (verify.ExitCode == 0 || !string.IsNullOrWhiteSpace(verify.StdOut))
            {
                var versionLine = verify.StdOut.Split('\n')[0].Trim();
                _logger.LogInformation("FFmpeg verified: {Version}", versionLine);
                progress?.Report(($"✓ {versionLine}", 100));
            }
            else
            {
                _logger.LogWarning(
                    "ffmpeg -version returned no output. Binary may not match the Pi's architecture.\n" +
                    "stderr: {Err}", verify.StdErr);

                progress?.Report(("Installed but version check produced no output — check architecture", 100));

                // Not thrown as an exception: the copy succeeded, the binary may still work;
                // the caller will see the warning in the terminal stream.
            }

            _logger.LogInformation(
                "FFmpeg deployment complete. Binary at {Dir}/ffmpeg", installDir);
        }

        // ── Private step runner ──────────────────────────────────────────────────

        /// <summary>
        /// Executes a command and throws <see cref="InvalidOperationException"/> if it exits non-zero.
        /// Used to build sequential deployment pipelines where any failure should abort.
        /// </summary>
        private async Task RunStep(string command, CancellationToken cancellationToken)
        {
            var result = await ExecuteCommandAsync(command, cancellationToken);
            if (!result.Succeeded)
            {
                _logger.LogError(
                    "Deployment step failed (exit {Code}): {Cmd} | stderr: {Err}",
                    result.ExitCode, command, result.StdErr.Trim());

                throw new InvalidOperationException(
                    "Step failed (exit " + result.ExitCode + "): " + command + "\n" + result.StdErr.Trim());
            }
        }

        // ── Convenience helpers ──────────────────────────────────────────────────

        /// <summary>Start tailing a remote log file. Returns a streaming session.</summary>
        public SshStreamSession TailLog(string remoteLogPath, int lines = 50)
        {
            _logger.LogInformation("Tailing log: {Path} (last {Lines} lines)", remoteLogPath, lines);
            return StartStreamingCommand($"tail -n {lines} -f \"{remoteLogPath}\"");
        }

        /// <summary>Get basic system info from the Pi in one call.</summary>
        public async Task<SshCommandResult> GetSystemInfoAsync(CancellationToken cancellationToken = default)
        {
            const string cmd = "echo '=== Hostname ===' && hostname && " +
                               "echo '=== OS ===' && cat /etc/os-release | grep PRETTY_NAME && " +
                               "echo '=== Uptime ===' && uptime && " +
                               "echo '=== Memory ===' && free -h && " +
                               "echo '=== Disk ===' && df -h /";
            return await ExecuteCommandAsync(cmd, cancellationToken);
        }

        // ── Private helpers ──────────────────────────────────────────────────────

        /// <summary>
        /// Configures the Pi 4B to use the stable PL011 UART0 (ttyAMA0) on GPIO 14/15
        /// by disabling Bluetooth. Debian 13 (Trixie) always uses /boot/firmware/config.txt.
        /// Returns true if changes were made and a reboot is required, false if already configured.
        /// </summary>
        /// <summary>
        /// Configures the Pi to use the stable PL011 UART0 (ttyAMA0) on GPIO 14/15 by:
        ///   - disabling Bluetooth (dtoverlay=disable-bt)
        ///   - disabling the hciuart service
        ///   - disabling and masking serial-getty@ttyAMA0.service
        ///
        /// Returns true if changes were made and a reboot is required,
        /// false if the system was already fully configured.
        /// </summary>
        public async Task<bool> ConfigureStableUartAsync(
            string password = "1234",
            CancellationToken cancellationToken = default)
        {
            EnsureConnected();

            const string bootConfig = "/boot/firmware/config.txt";
            var escapedPwd = password.Replace("'", "'\\''");

            _logger.LogInformation("Checking UART/Bluetooth configuration in {Config}", bootConfig);

            bool changesMade = false;

            // ------------------------------------------------------------
            // Check whether Bluetooth has already been disabled
            // ------------------------------------------------------------
            var overlayCheck = await ExecuteCommandAsync(
                $"grep -c '^dtoverlay=disable-bt' {bootConfig} || true",
                cancellationToken);

            bool overlayPresent =
                int.TryParse(overlayCheck.StdOut.Trim(), out int count) && count > 0;

            if (!overlayPresent)
            {
                _logger.LogInformation("Appending dtoverlay=disable-bt to {Config}", bootConfig);

                await RunStep(
                    $"printf '{escapedPwd}\\n' | sudo -S sh -c " +
                    $"\"echo -e '\\\\n# Disable Bluetooth to free UART0 (ttyAMA0) for GPIO 14/15\\\\ndtoverlay=disable-bt' >> {bootConfig}\"",
                    cancellationToken);

                changesMade = true;
            }
            else
            {
                _logger.LogInformation("Bluetooth overlay already configured.");
            }

            // ------------------------------------------------------------
            // Disable hciuart (safe even if it doesn't exist)
            // ------------------------------------------------------------
            _logger.LogInformation("Disabling hciuart service");

            await ExecuteCommandAsync(
                $"printf '{escapedPwd}\\n' | sudo -S systemctl disable hciuart 2>/dev/null || true",
                cancellationToken);

            await ExecuteCommandAsync(
                $"printf '{escapedPwd}\\n' | sudo -S systemctl stop hciuart 2>/dev/null || true",
                cancellationToken);

            // ------------------------------------------------------------
            // Check serial-getty state
            // ------------------------------------------------------------
            var gettyState = await ExecuteCommandAsync(
                "systemctl is-enabled serial-getty@ttyAMA0.service 2>/dev/null || echo disabled",
                cancellationToken);

            var enabledState = gettyState.StdOut.Trim();

            if (enabledState != "masked" && enabledState != "disabled")
            {
                _logger.LogInformation("Disabling serial-getty@ttyAMA0.service");

                await ExecuteCommandAsync(
                    $"printf '{escapedPwd}\\n' | sudo -S systemctl stop serial-getty@ttyAMA0.service 2>/dev/null || true",
                    cancellationToken);

                await ExecuteCommandAsync(
                    $"printf '{escapedPwd}\\n' | sudo -S systemctl disable serial-getty@ttyAMA0.service 2>/dev/null || true",
                    cancellationToken);

                await ExecuteCommandAsync(
                    $"printf '{escapedPwd}\\n' | sudo -S systemctl mask serial-getty@ttyAMA0.service 2>/dev/null || true",
                    cancellationToken);


                changesMade = true;
            }
            else
            {
                _logger.LogInformation("serial-getty@ttyAMA0.service already {State}.", enabledState);
            }

            gettyState = await ExecuteCommandAsync(
                "systemctl is-enabled serial-getty@serial0.service 2>/dev/null || echo disabled",
                cancellationToken);

            enabledState = gettyState.StdOut.Trim();

            if (enabledState != "masked" && enabledState != "disabled")
            {
                _logger.LogInformation("Disabling serial Getty on serial0");

                // Stop it immediately
                await ExecuteCommandAsync(
                    $"printf '{escapedPwd}\\n' | sudo -S systemctl stop serial-getty@serial0.service 2>/dev/null || true",
                    cancellationToken);

                // Disable it on boot
                await ExecuteCommandAsync(
                    $"printf '{escapedPwd}\\n' | sudo -S systemctl disable serial-getty@serial0.service 2>/dev/null || true",
                    cancellationToken);

                // Mask it so it can't be started automatically
                await ExecuteCommandAsync(
                    $"printf '{escapedPwd}\\n' | sudo -S systemctl mask serial-getty@serial0.service 2>/dev/null || true",
                    cancellationToken);


                changesMade = true;
            }
            else
            {
                _logger.LogInformation("serial-getty@serial0..service already {State}.", enabledState);
            }

            // ------------------------------------------------------------
            // Verify overlay exists
            // ------------------------------------------------------------
            var verify = await ExecuteCommandAsync(
                $"grep '^dtoverlay=disable-bt' {bootConfig}",
                cancellationToken);

            if (!verify.Succeeded || string.IsNullOrWhiteSpace(verify.StdOut))
            {
                _logger.LogError("UART configuration write could not be verified in {Config}", bootConfig);

                throw new InvalidOperationException(
                    $"Failed to verify dtoverlay=disable-bt was written to {bootConfig}.");
            }

            if (!changesMade)
            {
                _logger.LogInformation("UART0 (ttyAMA0) already fully configured.");
                return false;
            }

            _logger.LogInformation("UART0 configured successfully. Reboot required.");
            return true;
        }

        /// <summary>
        /// Issues a sudo reboot and polls until the device comes back online or the timeout elapses.
        /// The SSH connection drop immediately after the reboot command is issued is expected behaviour.
        /// </summary>
        public async Task RebootAndWaitAsync(
            string password = "1234",
            int waitSeconds = 30,
            CancellationToken cancellationToken = default)
        {
            EnsureConnected();

            var escapedPwd = password.Replace("'", "'\\''");

            _logger.LogInformation("Issuing reboot command to {Host}", Host);

            // The connection will drop the moment reboot executes — this is expected
            try
            {
                await ExecuteCommandAsync(
                    $"printf '{escapedPwd}\\n' | sudo -S reboot",
                    cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "SSH connection closed during reboot — this is expected.");
            }

            // Give the Pi time to actually go down before polling
            _logger.LogInformation("Waiting 10 seconds for {Host} to go down...", Host);
            await Task.Delay(TimeSpan.FromSeconds(10), cancellationToken);

            var deadline = DateTime.UtcNow.AddSeconds(waitSeconds);

            _logger.LogInformation("Polling {Host} for up to {Seconds}s...", Host, waitSeconds);

            while (DateTime.UtcNow < deadline)
            {
                cancellationToken.ThrowIfCancellationRequested();

                try
                {
                    // Reconnect the clients fresh — the old connection is dead
                    await ConnectAsync(Host, Username, password, Port, cancellationToken);

                    // Lightweight echo to confirm the shell is responsive
                    var ping = await ExecuteCommandAsync("echo online", cancellationToken);

                    if (ping.StdOut.Trim() == "online")
                    {
                        _logger.LogInformation("{Host} is back online.", Host);
                        return;
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "{Host} not yet available — retrying in 3s.", Host);
                    DisposeClients();
                }

                await Task.Delay(TimeSpan.FromSeconds(3), cancellationToken);
            }

            throw new TimeoutException(
                $"Device '{Host}' did not come back online within {waitSeconds} seconds after reboot.");
        }

        private void EnsureConnected()
        {
            if (!IsConnected)
                throw new InvalidOperationException(
                    $"SSH is not connected. Call ConnectAsync first. (Last host: {Host})");
        }

        private void DisposeClients()
        {
            _sshClient?.Dispose();
            _sshClient = null;
            _sftpClient?.Dispose();
            _sftpClient = null;
        }

        public async ValueTask DisposeAsync()
        {
            if (IsConnected)
                await DisconnectAsync();

            _connectionLock.Dispose();
        }
    }
}