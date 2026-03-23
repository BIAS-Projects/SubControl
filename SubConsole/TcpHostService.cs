using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using SubConsole.Helpers;
using SubConsole.Models;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace SubConsole.Services;

public class TcpHostService : BackgroundService
{
    private readonly ILogger<TcpHostService> _logger;
    private readonly TcpListener _listener;
    private readonly SerialPortManagerService _serialManager;
    private readonly WebcamManagerService _webcamManager;


    private readonly ConcurrentDictionary<TcpClient, ClientState> _clients = new();

    public List<string> CommPorts { get; set; } = new();
    public string CamerasOnCommand { get; set; } = @"$PBLUTP,S,PWR,CTRL,ON,15*29";

    public TcpHostService(ILogger<TcpHostService> logger,
                          SerialPortManagerService serial,
                          WebcamManagerService webcamManager)
    {
        _logger = logger;
        _listener = new TcpListener(IPAddress.Any, 9000);
        _serialManager = serial;
        _webcamManager = webcamManager;
    }

    // ── BACKGROUND SERVICE ENTRY POINT ───────────────────────────────────────

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var devices = await UsbDeviceEnumerator.GetUsbDevicesAsync();
        foreach (var d in devices)
            _logger.LogInformation("USB device: {Description}  VID:{VendorId} PID:{ProductId}",
                d.Description, d.VendorId, d.ProductId);

        var commPorts = await SerialPortManagerService.GetAvailablePortsAsync();
        foreach (var port in commPorts)
            _logger.LogInformation("Comm port: {Port}", port);

        var usbCommPorts = await UsbSerialPortMapper.GetUsbSerialPortsAsync();
        foreach (var port in usbCommPorts)
            _logger.LogInformation("USB serial port: VID:{VendorId} {PortName} DID:{DeviceId} PID:{ProductId} SN:{SerialNumber}",
                port.VendorId, port.PortName, port.DeviceId, port.ProductId, port.SerialNumber);

        _listener.Start();
        _logger.LogInformation("TCP Server started on port 9000");

        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                // Spin up a reader task for every open serial port
                foreach (var portName in _serialManager.OpenPorts)
                {
                    var port = _serialManager.GetPort(portName);
                    if (port == null) continue;
                    _ = Task.Run(() => ConsumePort(portName, port, stoppingToken), stoppingToken);
                }

                var client = await _listener.AcceptTcpClientAsync(stoppingToken);

                _logger.LogInformation("Client connected {Endpoint}", client.Client.RemoteEndPoint);

                var state = new ClientState(client);
                _clients[client] = state;

                // Redirect video streams to the connecting client's IP
                if (client.Client.RemoteEndPoint is IPEndPoint remoteEp)
                {
                    var clientIp = remoteEp.Address.ToString();

                    // Map IPv6-mapped IPv4 (::ffff:192.168.x.x) back to plain IPv4
                    if (remoteEp.Address.IsIPv4MappedToIPv6)
                        clientIp = remoteEp.Address.MapToIPv4().ToString();

                    _logger.LogInformation("Redirecting video streams to client IP {IP}", clientIp);
                    _ = Task.Run(() => _webcamManager.RedirectStreamsAsync(clientIp), stoppingToken);
                }

                await Task.Delay(2000, stoppingToken);

                _ = HandleClientAsync(client, stoppingToken);
            }
        }
        catch (OperationCanceledException) { }
        finally
        {
            await _serialManager.StopAsync(stoppingToken);
            _listener.Stop();
        }
    }

    // ── CLIENT RECEIVE LOOP ───────────────────────────────────────────────────

    private async Task HandleClientAsync(TcpClient client, CancellationToken token)
    {
        var stream = client.GetStream();
        var buffer = new byte[4096];
        var sb = new StringBuilder();

        try
        {
            while (!token.IsCancellationRequested)
            {
                int bytesRead = await stream.ReadAsync(buffer, token);
                if (bytesRead == 0)
                    break;  // client disconnected cleanly

                sb.Append(Encoding.UTF8.GetString(buffer, 0, bytesRead));
                string current = sb.ToString();
                int eomIndex;

                while ((eomIndex = current.IndexOf(TcpProtocol.EOM, StringComparison.Ordinal)) >= 0)
                {
                    // Pull one complete frame out of the buffer
                    var frame = current[..eomIndex];
                    current = current[(eomIndex + TcpProtocol.EOM.Length)..];

                    // Parse  <id>|<command>
                    // The client always sends frames in this format (see TcpSocketService).
                    var sepIdx = frame.IndexOf(TcpProtocol.SEP, StringComparison.Ordinal);
                    var id = sepIdx >= 0 ? frame[..sepIdx] : frame;
                    var command = sepIdx >= 0 ? frame[(sepIdx + 1)..] : string.Empty;

                    // Dispatch on a thread-pool thread so the receive loop is never blocked
                    _ = Task.Run(() => DispatchAndAckAsync(client, id, command, token), token);
                }

                sb.Clear();
                sb.Append(current);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Client error from {Endpoint}", client.Client.RemoteEndPoint);
        }
        finally
        {
            CleanupClient(client);
        }
    }

    // ── DISPATCH + ACK/NACK ───────────────────────────────────────────────────
    //
    // Every inbound frame gets exactly one reply:
    //   ACK|<id><EOM>          – command accepted and completed
    //   NACK|<id>|<reason><EOM> – command failed
    //
    // If the command also produces a data payload the result is sent as a
    // separate frame AFTER the ACK so the client can complete its ACK-wait
    // before processing the result:
    //   <id>|<result><EOM>

    private async Task DispatchAndAckAsync(TcpClient client,
                                            string id,
                                            string command,
                                            CancellationToken token)
    {
        try
        {
            string result = await HandleTCPCommand(client, command, token);

            // Always ACK first so the client's timeout is satisfied immediately
            await SendAsync(client, $"{TcpProtocol.ACK}{TcpProtocol.SEP}{id}{TcpProtocol.EOM}", token);

            // Send data payload as a separate frame if there is one
            if (!string.IsNullOrEmpty(result) && result != TcpProtocol.SuccessString)
                await SendAsync(client, $"{id}{TcpProtocol.SEP}{result}{TcpProtocol.EOM}", token);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Command '{Command}' (id={Id}) failed", command, id);

            try
            {
                await SendAsync(client,
                    $"{TcpProtocol.NACK}{TcpProtocol.SEP}{id}{TcpProtocol.SEP}{ex.Message}{TcpProtocol.EOM}",
                    token);
            }
            catch (Exception sendEx)
            {
                // If we can't even send the NACK the socket is likely dead
                _logger.LogError(sendEx, "Failed to send NACK for command '{Command}'", command);
            }
        }
    }

    // ── COMMAND HANDLER ───────────────────────────────────────────────────────
    //
    // Returns SuccessString for commands that have no data payload to return,
    // or a non-empty string that will be forwarded as a data frame to the client.
    // Throw an exception to send a NACK instead.

    private async Task<string> HandleTCPCommand(TcpClient client,
                                                 string command,
                                                 CancellationToken token)
    {
        _logger.LogDebug("Handling command: {Command}", command);

        switch (command)
        {
            case "GET USBCOMMPORTS":
                return command + TcpProtocol.CommandSeparatorChar + await BuildUSBCommPortList();

            case "GET FEATURES":
                // TODO: return feature flags
                return command + TcpProtocol.CommandSeparatorChar + TcpProtocol.SuccessString;

            case "START TOM CAM":
                await TOMStartAllSystems(client, token);
                return command + TcpProtocol.CommandSeparatorChar + TcpProtocol.SuccessString;

            case "STOP TOM":
                // TODO: implement TOM shutdown sequence
                return command + TcpProtocol.CommandSeparatorChar + TcpProtocol.SuccessString;

            default:
                _logger.LogWarning("Unknown command received: '{Command}'", command);
                throw new InvalidOperationException($"Unknown command: '{command}'");
        }
    }

    // ── SERIAL PORT CONSUMER ──────────────────────────────────────────────────

    private async Task ConsumePort(string portName, SerialPortWorker port, CancellationToken token)
    {
        try
        {
            await foreach (var line in port.Reader.ReadAllAsync(token))
            {
                _logger.LogDebug("{PortName} RX: {Line}", portName, line);
                Console.WriteLine($"{portName} RX: {line}");
            }
        }
        catch (OperationCanceledException) { }
    }

    // ── RS-232 COMMAND HANDLER (called externally / from tests) ───────────────

    public async Task HandleRS232Command(string portName, string command, string data)
    {
        if (command == "OPEN")
            await _serialManager.OpenPortAsync(portName, 115200);

        if (command == "CLOSE")
            await _serialManager.ClosePortAsync(portName);

        if (command.StartsWith("SEND"))
        {
            var serialPort = _serialManager.GetPort(portName);
            if (serialPort != null)
                await serialPort.WriteAsync(data + "\n\r", CancellationToken.None);
        }
    }

    // ── SEND HELPERS ──────────────────────────────────────────────────────────

    public async Task SendAsync(TcpClient client, string message, CancellationToken token)
    {
        if (!client.Connected) return;
        var bytes = Encoding.UTF8.GetBytes(message);
        await client.GetStream().WriteAsync(bytes, token);
    }

    // ── CLEANUP ───────────────────────────────────────────────────────────────

    private void CleanupClient(TcpClient client)
    {
        if (_clients.TryRemove(client, out _))
        {
            client.Close();
            _logger.LogInformation("Client disconnected — reverting video streams to localhost");

            if (_clients.IsEmpty)
                _ = Task.Run(() => _webcamManager.RedirectStreamsAsync("127.0.0.1"));
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("Stopping TCP server");

        foreach (var client in _clients.Keys)
            client.Close();

        _listener.Stop();
        await base.StopAsync(cancellationToken);
    }

    // ── COMMAND IMPLEMENTATIONS ───────────────────────────────────────────────

    /// <summary>
    /// Builds a newline-separated list of USB serial ports and returns it as a
    /// string so DispatchAndAckAsync can forward it to the client as a data frame.
    /// </summary>
    private static async Task<string> BuildUSBCommPortList()
    {
        var usbCommPorts = await UsbSerialPortMapper.GetUsbSerialPortsAsync();
        string result = string.Empty;
        if (!usbCommPorts.Any())
            return string.Empty;
        foreach (var usbPort in usbCommPorts)
        {
            result += "{";
            result += $"{usbPort.PortName},";
            result += $"{usbPort.VendorId},";
            result += $"{usbPort.ProductId},";
            result += $"{usbPort.SerialNumber},";
            result += $"{usbPort.Description},";
            result += $"{usbPort.DeviceId},";
            result += "}";
        }

        return string.Join("\r\n", usbCommPorts.Select(p => p.ToString()));
    }

    /// <summary>
    /// Sends the camera-on command to TOM via the relevant serial port and
    /// optionally performs any other startup sequencing.
    /// Returns the list of active cameras for the caller to forward if needed.
    /// </summary>
    private async Task<List<string>> TOMStartAllSystems(TcpClient client, CancellationToken token)
    {
        // Send turn-cameras-on message to TOM over the appropriate serial port.
        // Replace "COM5" with the dynamically-resolved TOM port when that mapping
        // is available via UsbSerialPortMapper.
        await HandleRS232Command("COM5", "SEND", CamerasOnCommand);

        // TODO: wait for TOM to confirm cameras are live, then return the
        //       camera list so the client can begin receiving streams.
        return new List<string>();
    }

    // ── PRIVATE TYPES ─────────────────────────────────────────────────────────

    private sealed class ClientState
    {
        public TcpClient Client { get; }

        public ClientState(TcpClient client)
        {
            Client = client;
        }
    }
}