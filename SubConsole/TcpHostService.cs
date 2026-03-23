using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using SubConsole.Helpers;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Numerics;
using System.Reflection.Metadata.Ecma335;
using System.Runtime.InteropServices;
using System.Text;
using static SQLite.SQLite3;
using static System.Net.Mime.MediaTypeNames;

namespace SubConsole.Services;

public class TcpHostService : BackgroundService
{
    private readonly ILogger<TcpHostService> _logger;
    private readonly TcpListener _listener;
    private readonly SerialPortManagerService _serialManager;
    private readonly WebcamManagerService _webcamManager;
    private const string successString = "OK";

    private readonly ConcurrentDictionary<TcpClient, ClientState> _clients = new();

    public List<string> CommPorts { get; set; }
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

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Comm port names in windows 


        //await HandleCommand("COM5", "OPEN", "");
        //await HandleCommand("COM6", "OPEN", "");
        //await HandleCommand("COM7", "OPEN", "");
        //await HandleCommand("COM8", "OPEN", "");

        //// Turn on TOM
        //await HandleCommand("COM5", "SEND", @"$PBLUTP,S,PWR,CTRL,ON,15*29");


        var devices = await UsbDeviceEnumerator.GetUsbDevicesAsync();
        foreach (var d in devices)
            _logger.LogInformation($"**************************{d.Description}  VID:{d.VendorId} PID:{d.ProductId}");

        var commPorts = await SerialPortManagerService.GetAvailablePortsAsync();
        foreach (var port in commPorts)
            _logger.LogInformation($"**************************{port}");

        var usbCommPorts = await UsbSerialPortMapper.GetUsbSerialPortsAsync();
        foreach (var port in usbCommPorts)
            _logger.LogInformation($"**************************{port.VendorId} {port.PortName} {port.DeviceId} {port.ProductId} {port.SerialNumber}");

        _listener.Start();
        _logger.LogInformation("TCP Server started on port 9000");

        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                // Listen on all serial ports
                foreach (var portName in _serialManager.OpenPorts)
                {
                    var port = _serialManager.GetPort(portName);
                    if (port == null) continue;
                    _ = Task.Run(() => ConsumePort(portName, port, stoppingToken), stoppingToken);
                }

                var client = await _listener.AcceptTcpClientAsync(stoppingToken);

                _logger.LogInformation("Client connected {Endpoint}",
                    client.Client.RemoteEndPoint);

                var state = new ClientState(client);
                _clients[client] = state;

                // Extract the client's IP and redirect all video streams to it.
                // The client is on the other end of the TCP control connection so
                // it can also receive the UDP RTP video on the same IP.
                if (client.Client.RemoteEndPoint is IPEndPoint remoteEp)
                {
                    var clientIp = remoteEp.Address.ToString();

                    // Map IPv6-mapped IPv4 addresses (::ffff:192.168.x.x) back to
                    // plain IPv4 so udpsink gets a usable destination address.
                    if (remoteEp.Address.IsIPv4MappedToIPv6)
                        clientIp = remoteEp.Address.MapToIPv4().ToString();

                    _logger.LogInformation(
                        "Redirecting video streams to client IP {IP}", clientIp);

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

    private async Task HandleClientAsync(TcpClient client, CancellationToken token)
    {

        var stream = client.GetStream();
        var buffer = new byte[4096];

        try
        {

            var sb = new StringBuilder();

            while (!token.IsCancellationRequested)
            {
                int bytesRead = await stream.ReadAsync(buffer, token);
                if (bytesRead == 0)
                    break;

                sb.Append(Encoding.UTF8.GetString(buffer, 0, bytesRead));

                string current = sb.ToString();
                int eomIndex;

                while ((eomIndex = current.IndexOf(TcpProtocol.EOM, StringComparison.Ordinal)) >= 0)
                {
                    // Extract one complete message
                    string message = current.Substring(0, eomIndex);

                    //Always ACK
                //    await SendAsync(client, $"{TcpProtocol.ACK + TcpProtocol.EOM}", token);

                   string result = await HandleTCPCommand(client, message, token);
                   //await HandleTCPCommand(client, token, message);

                    await SendAsync(client, $"{result + TcpProtocol.EOM}", token);

                    // Remove processed message + EOM
                    current = current.Substring(eomIndex + TcpProtocol.EOM.Length);
                }

                sb.Clear();
                sb.Append(current);
            }

            //while (!token.IsCancellationRequested)
            //{ 


            //    int bytes = await stream.ReadAsync(buffer, token);

            //    if (bytes == 0)
            //        break;

            //    var text = Encoding.UTF8.GetString(buffer, 0, bytes);

            //    //Always ACK
            //    await SendAsync(client, $"{TcpProtocol.ACK + TcpProtocol.EOM}", token);

            //    string result = await HandleTCPCommand(token, text);

            //    await SendAsync(client, $"Echo: {result}", token);
            //}
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Client error");
        }
        finally
        {
            CleanupClient(client);
        }
    }


    private async Task<string> HandleTCPCommand(TcpClient client, string text, CancellationToken token)
    {
        string result = "";
        try
        {
 
            switch (text)
            {
                case ("GET USBCOMMPORTS"):

                    result = await SendUSBCommPortListToTCPClient(client, token);
                    break;

                case ("GET FEATURES"):
                    break;

                case ("START TOM CAM"):
                    await TOMStartAllSystems(client, token);
                    break;

   
                case ("STOP TOM"):
                    break;
      
            }
            return result;

        }
        catch (Exception ex)
        {
            return (ex.Message);
        }

        }

    private async Task ConsumePort(string portName, SerialPortWorker port, CancellationToken token)
    {
        try
        {
            await foreach (var line in port.Reader.ReadAllAsync(token))
                Console.WriteLine($"{portName} RX: {line}");
        }
        catch (OperationCanceledException) { }
    }

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

    public async Task SendAsync(TcpClient client, string message, CancellationToken token)
    {
        if (!client.Connected) return;
        var bytes = Encoding.UTF8.GetBytes(message);
        await client.GetStream().WriteAsync(bytes, token);
    }

    private void CleanupClient(TcpClient client)
    {
        if (_clients.TryRemove(client, out _))
        {
            client.Close();
            _logger.LogInformation("Client disconnected — reverting video streams to localhost");

            // If no other clients remain, revert streams back to localhost so
            // they don't spray UDP into the network with no receiver.
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

    private class ClientState
    {
        public TcpClient Client { get; }

        public ClientState(TcpClient client)
        {
            Client = client;
        }
    }


    private async Task<string> SendUSBCommPortListToTCPClient(TcpClient client, CancellationToken token)
    {
        var usbCommPorts = await UsbSerialPortMapper.GetUsbSerialPortsAsync();
        string ports = "";
        foreach (var port in usbCommPorts)
        {
            ports += ($"{port}\r\n");
        }
        await SendAsync(client, ports, token);         
        return successString;
    }


    private async Task<List<string>> TOMStartAllSystems(TcpClient client, CancellationToken token)
    {


        //Send turn cameras on message to TOM (camera streaming is always on and should see the cameras and begin streaming)



        //Return a list of cameras and their ports to the client
        return null;

    }

}