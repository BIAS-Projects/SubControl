using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using SubConsole.Helpers;
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

    public TcpHostService(ILogger<TcpHostService> logger, SerialPortManagerService serial, WebcamManagerService webcamManager)
    {
        _logger = logger;
        _listener = new TcpListener(IPAddress.Any, 9000);
        _serialManager = serial;
        _webcamManager = webcamManager;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await HandleCommand("COM5","OPEN","");
        await HandleCommand("COM6","OPEN","");
        await HandleCommand("COM7","OPEN","");
        await HandleCommand("COM8","OPEN","");

        //Turn on TOM
        await HandleCommand("COM5","SEND", @"$PBLUTP,S,PWR,CTRL,ON,15*29");

      //  Thread.Sleep(10000);

        var devices = await UsbDeviceEnumerator.GetUsbDevicesAsync();

        //foreach (var d in devices)
        //{
        //    Console.WriteLine($"{d.Description}  VID:{d.VendorId} PID:{d.ProductId}");
        //}


        //await _webcamManager.StartWebcamAsync("/dev/video0", "0.0.0.0", 5000);
        //await _webcamManager.StartWebcamAsync("/dev/video1", "0.0.0.0", 5001);


        _listener.Start();
        _logger.LogInformation("TCP Server started on port 9000");

        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {

                //Listen on all serial ports

                foreach (var portName in _serialManager.OpenPorts)
                {
                    var port = _serialManager.GetPort(portName);

                    if (port == null)
                        continue;

                    _ = Task.Run(() => ConsumePort(portName, port, stoppingToken), stoppingToken);
                }

                var client = await _listener.AcceptTcpClientAsync(stoppingToken);

                _logger.LogInformation("Client connected {endpoint}",
                    client.Client.RemoteEndPoint);

                var state = new ClientState(client);

                _clients[client] = state;

                

                await Task.Delay(2000, stoppingToken);


                _ = HandleClientAsync(state, stoppingToken);
            }
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {

            await _serialManager.StopAsync(stoppingToken);
            _listener.Stop();
        }
    }

    private async Task HandleClientAsync(ClientState state, CancellationToken token)
    {
        var client = state.Client;
        var stream = client.GetStream();

        var buffer = new byte[4096];

        try
        {
            while (!token.IsCancellationRequested)
            {
                int bytes = await stream.ReadAsync(buffer, token);

                if (bytes == 0)
                    break;

                var text = Encoding.UTF8.GetString(buffer, 0, bytes);

                await SendAsync(client, $"Echo: {text}", token);
            }
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


    private async Task ConsumePort(string portName, SerialPortWorker port, CancellationToken token)
    {
        try
        {
            await foreach (var line in port.Reader.ReadAllAsync(token))
            {
                Console.WriteLine($"{portName} RX: {line}");
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    public async Task HandleCommand(string port, string command, string data)
    {
        if (command == "OPEN")
            await _serialManager.OpenPortAsync(port, 115200);

        if (command == "CLOSE")
            await _serialManager.ClosePortAsync(port);

        if (command.StartsWith("SEND"))
        {
            var serialPort = _serialManager.GetPort(port);

            if (serialPort != null)
                await serialPort.WriteAsync(data+ "\n\r", CancellationToken.None);
        }
    }


    public async Task SendAsync(TcpClient client, string message, CancellationToken token)
    {
        if (!client.Connected)
            return;

        var bytes = Encoding.UTF8.GetBytes(message);

        await client.GetStream().WriteAsync(bytes, token);
    }

    private void CleanupClient(TcpClient client)
    {
        if (_clients.TryRemove(client, out _))
        {
            client.Close();
            _logger.LogInformation("Client disconnected");
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



}