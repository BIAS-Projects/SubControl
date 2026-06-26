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
                _logger.LogError(ex, "Failed to connect to {Username}@{Host}:{Port} with key", username, host, port);
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
        /// Ideal for deploying .NET console app binaries.
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
                    // SSH.NET calls this with bytes uploaded
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
        /// Uploads all files in <paramref name="localFolder"/> to a staging path,
        /// transfers them safely to <paramref name="remoteFolder"/>, then marks the entry point executable.
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

            // Escape single quotes in password to prevent shell context breaking
            var escapedPwd = password.Replace("'", "'\\''");
            var targetFolder = remoteFolder.TrimEnd('/');

            // 1. Ensure the protected remote destination directory exists via sudo
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

                // The final system path and a safe temporary staging path
                var finalRemotePath = $"{targetFolder}/{relativePath}";
                var tmpStagingPath = $"/tmp/dotnet_deploy_{Guid.NewGuid():N}";
                destinationFolder = finalRemotePath;

                // 2. Ensure the destination subdirectory exists inside the system directory
                var finalRemoteDir = Path.GetDirectoryName(finalRemotePath)?.Replace('\\', '/');
                if (!string.IsNullOrEmpty(finalRemoteDir))
                {
                    await ExecuteCommandAsync($"printf '{escapedPwd}\\n' | sudo -S mkdir -p \"{finalRemoteDir}\"", cancellationToken);
                }

                progress?.Report((relativePath, (double)i / files.Length * 100.0));

                // 3. Upload via SFTP to the open /tmp folder first (since SFTP cannot use sudo directly)
                await UploadFileAsync(localFile, tmpStagingPath, cancellationToken: cancellationToken);

                // 4. Move the file from /tmp to its final protected location using sudo
                await ExecuteCommandAsync($"printf '{escapedPwd}\\n' | sudo -S mv \"{tmpStagingPath}\" \"{finalRemotePath}\"", cancellationToken);
            }

            // Check the fils are in the destination dierctory:
            var contents = await ExecuteCommandAsync($"ls -laR \"{targetFolder}\"", cancellationToken);

            _logger.LogInformation("Current files in {installDir}:\n{Files}", targetFolder, contents.StdOut);

            progress?.Report(($"Current files in {targetFolder} {contents.StdOut}", 100));


            // 1. Force your service user as the absolute recursive owner of the entire deployment folder structure
            await ExecuteCommandAsync($"printf '{escapedPwd}\\n' | sudo -S chown -R {Username}:{Username} \"{remoteFolder}\"", cancellationToken);

            // 2. Set directory guidelines: Owner can Read/Write/Execute, Group/Others can read/enter
            await ExecuteCommandAsync($"printf '{escapedPwd}\\n' | sudo -S chmod -R 755 \"{remoteFolder}\"", cancellationToken);

            // 3. Explicitly guarantee the main application binary has execution rights flags
            await ExecuteCommandAsync($"printf '{escapedPwd}\\n' | sudo -S chmod +x \"{remoteFolder}\"/{executableName}", cancellationToken);

            // Check the folder permissions are in the destination dierctory:
            contents = await ExecuteCommandAsync($"ls -la \"{remoteFolder}\"", cancellationToken);

            _logger.LogInformation("Current files in {installDir}:\n{Files}", remoteFolder, contents.StdOut);

            progress?.Report(($"Current files in {remoteFolder} {contents.StdOut}", 100));


            // 5. Mark the entry-point executable as executable using sudo
            var exePath = $"{targetFolder}/{executableName}";
            await ExecuteCommandAsync($"printf '{escapedPwd}\\n' | sudo -S chmod +x \"{exePath}\"", cancellationToken);

            _logger.LogInformation("Deployment complete. Entry point: {ExePath}", exePath);
            progress?.Report((executableName, 100.0));
        }



        /// <summary>
        /// Installs and configures the deployed .NET console application as a systemd service,
        /// enabling it to run automatically on system boot.
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

            // 1. Define the systemd configuration lines for a modern .NET environment
            var unitLines = new[]
            {
             "[Unit]",
             $"Description=.NET Application: {serviceName}",
             "After=network.target",
             "",
             "[Service]",
             "Type=notify", // Recommended for .NET Core apps using Microsoft.Extensions.Hosting
             $"User={serviceUser}",
             $"WorkingDirectory={targetFolder}",
             $"ExecStart={binaryPath}",
             "Restart=on-failure",
             "RestartSec=5",
             "StandardOutput=journal",
             "StandardError=journal",
             // Optional: Keeps .NET from flooding production logging frameworks with development details
             "Environment=DOTNET_ENVIRONMENT=Production",
             "",
             "[Install]",
             "WantedBy=multi-user.target"
         };

            // 2. Base64 encode the systemd payload within C# to prevent single/double quote injection breaks
            var rawUnitContent = string.Join("\n", unitLines) + "\n";
            var base64UnitBytes = System.Text.Encoding.UTF8.GetBytes(rawUnitContent);
            var base64UnitString = Convert.ToBase64String(base64UnitBytes);

            // 3. Securely write the unit file to the protected systemd path using sudo elevation
            _logger.LogInformation("Writing service unit file to {Path}", unitPath);
            await ExecuteCommandAsync($"printf '{escapedPwd}\\n' | sudo -S sh -c 'echo \"{base64UnitString}\" | base64 -d > {unitPath}'", cancellationToken);

            // 4. Force systemd to scan the disk cache and register the new configuration file
            await ExecuteCommandAsync($"printf '{escapedPwd}\\n' | sudo -S systemctl daemon-reload", cancellationToken);

            // 5. Enable the background process so it boots automatically whenever the Pi turns on
            _logger.LogInformation("Enabling service on system boot...");
            await ExecuteCommandAsync($"printf '{escapedPwd}\\n' | sudo -S systemctl enable {serviceName}.service", cancellationToken);

            // 6. Fire up the engine immediately
            _logger.LogInformation("Starting service...");
            await ExecuteCommandAsync($"printf '{escapedPwd}\\n' | sudo -S systemctl restart {serviceName}.service", cancellationToken);

            // 7. Verify the process launched successfully
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

            // Escape single quotes in password if any exist to avoid breaking shell arguments
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

        //    await RunStep($"printf '{escapedPwd}\\n' | sudo -S mkdir -p {installDir}", cancellationToken);
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

            // --strip-components=1 flattens the versioned top-level folder in the tarball
            //  await RunStep($"printf '{escapedPwd}\\n' | sudo -S tar -xzf {remoteTar} -C {installDir} --strip-components=1", cancellationToken);
            await RunStep($"printf '{escapedPwd}\\n' | sudo -S tar -xzf {remoteTar} -C {installDir}", cancellationToken);
            await RunStep($"rm -f {remoteTar}", cancellationToken);

            progress?.Report(("Extraction complete", 55));
            _logger.LogInformation("Binary expected at: {Path}", binaryPath);


            // Run this right after the extraction step to see exactly what is inside /opt/mediamtx:
            var contents = await ExecuteCommandAsync($"ls -la {installDir}", cancellationToken);
            _logger.LogInformation("Current files in {installDir}:\n{Files}", installDir, contents.StdOut);

            progress?.Report(($"Current files in {installDir} {contents.StdOut}", 55));





            // ── Step 3: Permissions and capabilities ──────────────────────────
            progress?.Report(("Setting permissions and capabilities...", 60));
            _logger.LogInformation("Step 4/6 - Granting capabilities to {Binary}", binaryPath);

            await RunStep($"printf '{escapedPwd}\\n' | sudo -S chmod +x {binaryPath}", cancellationToken);
            await RunStep($"printf '{escapedPwd}\\n' | sudo -S chown {serviceUser}:{serviceUser} {binaryPath}", cancellationToken);

            // CAP_NET_BIND_SERVICE  - bind ports < 1024 (RTSP 554, HLS 80)
            // CAP_NET_ADMIN         - network interface / multicast management
            // CAP_NET_RAW           - raw sockets for some stream sources
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

                // 1. Join the array lines with standard Linux line endings (\n)
                var rawConfigContent = string.Join("\n", configLines) + "\n";

                // 2. Base64 encode the string payload within C#
                var base64ConfigBytes = System.Text.Encoding.UTF8.GetBytes(rawConfigContent);
                var base64ConfigString = Convert.ToBase64String(base64ConfigBytes);

                // 3. Pipe the base64 string directly into base64 -d as root to write the file cleanly
                await RunStep($"printf '{escapedPwd}\\n' | sudo -S sh -c 'echo \"{base64ConfigString}\" | base64 -d > {configPath}'", cancellationToken);

                // Debug: Verify the file actually exists on disk now and show its details
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

            // 1. Join array lines with actual Linux line-endings (\n)
            var rawUnitContent = string.Join("\n", unitLines) + "\n";

            // 2. Base64 encode the entire payload within C#
            var base64UnitBytes = Encoding.UTF8.GetBytes(rawUnitContent);
            var base64UnitString = Convert.ToBase64String(base64UnitBytes);

            // 3. Write it to disk by piping the base64 string directly into base64 -d as root
            await RunStep($"printf '{escapedPwd}\\n' | sudo -S sh -c 'echo \"{base64UnitString}\" | base64 -d > {unitPath}'", cancellationToken);

            // 4. Force systemd to cache your new configuration file
            await RunStep($"printf '{escapedPwd}\\n' | sudo -S systemctl daemon-reload", cancellationToken);

            // Debug: Verify the file actually exists on disk now
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