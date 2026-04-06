using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.VisualBasic;
using SubConsole.Helpers;
using SubConsole.Models;
using SubConsole.Services.Serial;
using SubConsole.Services.Serial.Workers;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Reflection.Metadata.Ecma335;
using System.Text;
using System.Xml.XPath;

namespace SubConsole.Services;

public class TcpHostService : BackgroundService
{
    private readonly ILogger<TcpHostService> _logger;
    private readonly TcpListener _listener;
    private readonly SerialPortManagerService _serialManager;
    private readonly WebcamManagerService _webcamManager;

    public enum SerialWorkerType
    {
        Text,
        Flir
    }


    private readonly ConcurrentDictionary<TcpClient, ClientState> _clients = new();

    //public List<string> CommPorts { get; set; } = new();
    //public string CamerasOnCommand { get; set; } = @"$PBLUTP,S,PWR,CTRL,ON,15*29";

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

                    if (port is SerialPortWorker textPort)
                    {
                        _ = Task.Run(() => ConsumePort(portName, textPort, stoppingToken), stoppingToken);
                    }
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
    //   ACK|<id><EOM>          – command accepted
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

            var result = await HandleTCPCommand(client, command, token);

            // Always ACK first so the client's timeout is satisfied immediately
            await SendAsync(client, $"{TcpProtocol.ACK}{TcpProtocol.SEP}{id}{TcpProtocol.EOM}", token);

            // Send data payload as a separate frame if there is one
            if (!string.IsNullOrEmpty(result.Value) && result.Value != TcpProtocol.SuccessString)
                await SendAsync(client, $"{id}{TcpProtocol.SEP}{result.Value}{TcpProtocol.EOM}", token);

     //       if(!result.IsSuccess)


            //string result = await HandleTCPCommand(client, command, token);

            //// Always ACK first so the client's timeout is satisfied immediately
            //await SendAsync(client, $"{TcpProtocol.ACK}{TcpProtocol.SEP}{id}{TcpProtocol.EOM}", token);

            //// Send data payload as a separate frame if there is one
            //if (!string.IsNullOrEmpty(result) && result != TcpProtocol.SuccessString)
            //    await SendAsync(client, $"{id}{TcpProtocol.SEP}{result}{TcpProtocol.EOM}", token);
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

    private async Task<OperationResultWithValue<string>> HandleTCPCommand(TcpClient client,
                                                 string command,
                                                 CancellationToken token)
    {
        _logger.LogDebug("Handling command: {Command}", command);

        switch (command)
        {
            case "GET USBCOMMPORTS":
                return await BuildUSBCommPortList();
            //    return OperationResultWithValue<string>.Success(command + TcpProtocol.CommandSeparatorChar + usbCommPortList.Value );

            case "GET FEATURES":
                // TODO: return feature flags

                return OperationResultWithValue<string>.Failure($"Unknown command: '{command}'");

            case "START TOM ALL":
                var startResult = await TOMStartAllSystems(client, token);
                if (startResult.IsSuccess)
                {
                    return OperationResultWithValue<string>.Success(command + TcpProtocol.CommandSeparatorChar + TcpProtocol.SuccessString);
                }
                else
                {
                  //  return OperationResultWithValue<string>.Failure(command + TcpProtocol.CommandSeparatorChar + TcpProtocol.NACK);
                    return OperationResultWithValue<string>.Failure($"Unknown command: '{command}'");
                }


            case "STOP TOM ALL":
                var stopResult = await TOMStopAllSystems(client, token);
                if (stopResult.IsSuccess)
                {
                    return OperationResultWithValue<string>.Success(command + TcpProtocol.CommandSeparatorChar + TcpProtocol.SuccessString);
                }
                else
                {
                    return OperationResultWithValue<string>.Failure(command + TcpProtocol.CommandSeparatorChar + TcpProtocol.SuccessString);
                }
            default:
                _logger.LogWarning("Unknown command received: '{Command}'", command);
                return OperationResultWithValue<string>.Failure($"Unknown command: '{command}'");
 
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

    public async Task<OperationResult> HandleRS232Command(string portName, int baudRate, string command, string data)
    {
        switch (command)
        {
            case "OPEN":
                var result = await _serialManager.OpenPortAsync(portName, baudRate, SerialWorkerType.Text);
                //or await _serialManager.OpenPortAsync(portName, baudRate, SerialWorkerType.Text);
                if (result.IsSuccess)
                // if (await _serialManager.OpenPortAsync(portName, 115200))
                {
                    return OperationResult.Success();
                }
                else
                {
                    return OperationResult.Failure($"Write to {portName} timed out");
                }

            case "CLOSE":
                return (await _serialManager.ClosePortAsync(portName));

            case "SEND":
                var serialPort = _serialManager.GetPort(portName);
                if (serialPort != null)
                {
                    return (await serialPort.WriteAsync(data + "\n\r", CancellationToken.None));

                }
                else
                {
                    return OperationResult.Failure($"Serial port {portName} is null");
                }

            default:
                _logger.LogWarning($"Unknown command received: {command}", command);
                return OperationResult.Failure($"Unknown command received: {command}");
                //  throw new InvalidOperationException($"Unknown command: '{command}'");
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
    /// 

    private static async Task<OperationResultWithValue<string>> BuildUSBCommPortList()
    {
      //  var usbCommPorts = await UsbSerialPortMapper.GetUsbSerialPortsAsync();
        var usbCommPorts = await UsbSerialPortMapper.GetUsbSerialPortsAsJsonAsync();

        if (!usbCommPorts.Any())
            return OperationResultWithValue<string>.Failure($"No ports found");
        else
            return OperationResultWithValue<string>.Success(usbCommPorts);

      //  return string.Join("\r\n", usbCommPorts.Select(p => p.ToString()));

    }


    /// <summary>
    /// Sends the camera-on command to TOM via the relevant serial port 
    /// </summary>
    private async Task<OperationResult> TOMStartAllSystems(TcpClient client, CancellationToken token)
    {
        var result = await HandleRS232Command(TOM.CommandPort, TOM.TomBaudCommandBaudRate, "OPEN","");
        // Send turn-cameras-on message to TOM over the appropriate serial port.
        //
        if (!result.IsSuccess)
        {
            return result;
        }
        result = await HandleRS232Command(TOM.CommandPort, TOM.TomBaudCommandBaudRate, "SEND", TOM.TurnOnAllSystemsCommand);
        if (!result.IsSuccess)
        {
            return result;
        }
        result = await HandleRS232Command(TOM.CommandPort, TOM.TomBaudCommandBaudRate, "CLOSE", "");
        return result;
    }


    /// <summary>
    /// Sends the camera-on command to TOM via the relevant serial port 
    /// </summary>
    private async Task<OperationResult> TOMStopAllSystems(TcpClient client, CancellationToken token)
    {
        var result = await HandleRS232Command(TOM.CommandPort, TOM.TomBaudCommandBaudRate, "OPEN", "");
        // Send turn-cameras-on message to TOM over the appropriate serial port.
        //
        if (!result.IsSuccess)
        {
            return result;
        }
        result = await HandleRS232Command(TOM.CommandPort, TOM.TomBaudCommandBaudRate, "SEND", TOM.TurnOffAllSystemsCommand);
        if (!result.IsSuccess)
        {
            return result;
        }
        result = await HandleRS232Command(TOM.CommandPort, TOM.TomBaudCommandBaudRate, "CLOSE", "");
        return result;
    }


    private async Task<OperationResult> FindTOMControlPort(CancellationToken token)
    {

        return OperationResult.Failure($"Not implemented");
        //send a test message to the comm port

        //if the test message passes return the comm port

        //if the test message fails try and find the comm port if its found else were update the stored settings
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