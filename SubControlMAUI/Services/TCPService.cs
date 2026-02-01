
//using CommunityToolkit.Mvvm.Messaging;
//using SubControlMAUI.Messages;
//using System;
//using System.Collections;
//using System.Collections.Generic;
//using System.Globalization;
//using System.Net;
//using System.Net.Sockets;
//using System.Text;




//namespace SubControlMAUI.Services
//{


//    public class TCPService
//    {
//        Task? _receiveTask;
//        CancellationTokenSource? _cts;
//        Socket handler;


//        public async Task StartAsync(string ipAddress, string port)
//        {


//            IPEndPoint ipEndPoint = CreateIPEndPointFromString(ipAddress + ":" + port);

//            using Socket listener = new(
//    ipEndPoint.AddressFamily,
//    SocketType.Stream,
//    ProtocolType.Tcp);

//            listener.Bind(ipEndPoint);
//            listener.Listen(100);


//               handler = await listener.AcceptAsync();
            


//            _cts = new CancellationTokenSource();


//            _receiveTask = Task.Run(() => ReceiveLoopAsync(handler, _cts.Token));




//            //while (true)
//            //{
//            //    // Receive message.
//            //    var buffer = new byte[1_024];
//            //    var received = await handler.ReceiveAsync(buffer, SocketFlags.None);
//            //    var response = Encoding.UTF8.GetString(buffer, 0, received);

//            //    var eom = "<|EOM|>";
//            //    if (response.IndexOf(eom) > -1 /* is end of message */)
//            //    {
//            //        WeakReferenceMessenger.Default
//            //                .Send(new TCPReceiveMessage($"Socket server received message: {response}"));


//            //        var ackMessage = "<|ACK|>";
//            //        var echoBytes = Encoding.UTF8.GetBytes(ackMessage);
//            //        await handler.SendAsync(echoBytes, 0);

//            //        WeakReferenceMessenger.Default
//            //.Send(new TCPReceiveMessage($"Socket server sent acknowledgment: {ackMessage}"));



//            //    }

//            //  }
//        }



//        private async Task ReceiveLoopAsync(Socket handler, CancellationToken token)
//        {

//            while (!token.IsCancellationRequested)
//             {


//                // Receive message.
//                var buffer = new byte[1_024];
//            var received = await handler.ReceiveAsync(buffer, SocketFlags.None);
//            var response = Encoding.UTF8.GetString(buffer, 0, received);

//            var eom = "<|EOM|>";
//            if (response.IndexOf(eom) > -1 /* is end of message */)
//            {
//                WeakReferenceMessenger.Default
//                        .Send(new TCPReceiveMessage($"Socket server received message: {response}"));


//                var ackMessage = "<|ACK|>";
//                var echoBytes = Encoding.UTF8.GetBytes(ackMessage);
//                await handler.SendAsync(echoBytes, 0);

//                WeakReferenceMessenger.Default
//        .Send(new TCPReceiveMessage($"Socket server sent acknowledgment: {ackMessage}"));
//            }
//        }
//        }



//        public async Task SendAsync(string ipAddress, string port, string message)
//        {
//            IPEndPoint ipEndPoint = CreateIPEndPointFromString(ipAddress + ":" + port);
//            using Socket client = new(
//    ipEndPoint.AddressFamily,
//    SocketType.Stream,
//    ProtocolType.Tcp);

//            await client.ConnectAsync(ipEndPoint);
//            while (true)
//            {
//                var eom = "<|EOM|>";
//                // Send message.
//                //  var message = "Hi friends 👋!<|EOM|>";
//                var messageBytes = Encoding.UTF8.GetBytes(message+ eom);
//                _ = await client.SendAsync(messageBytes, SocketFlags.None);
//                WeakReferenceMessenger.Default.Send(new TCPReceiveMessage($"Socket client sending: {message}"));


//                // Receive ack.
//                var buffer = new byte[1_024];
//                var received = await client.ReceiveAsync(buffer, SocketFlags.None);
//                var response = Encoding.UTF8.GetString(buffer, 0, received);
//                if (response == "<|ACK|>")
//                {
//                    WeakReferenceMessenger.Default.Send(new TCPReceiveMessage($"Socket client received acknowledgment: {response}"));

//                    break;
//                }
//                else
//                {
//                    WeakReferenceMessenger.Default.Send(new TCPReceiveMessage($"Socket server did not acknowledge: {response}"));

//                }


//            }

//            client.Shutdown(SocketShutdown.Both);
//        }


//        //public sealed class TCPService : IAsyncDisposable
//        //{
//        //private Socket? _socket;
//        //private CancellationTokenSource? _cts;
//        //private Task? _receiveTask;

//        //public async Task StartAsync(string ipAddress, string port)
//        //{
//        //    IPEndPoint endPoint = CreateIPEndPointFromString(ipAddress + ":" + port);
//        //    if (_socket != null)
//        //        throw new InvalidOperationException("TCPService already started.");

//        //    _cts = new CancellationTokenSource();

//        //    _socket = new Socket(
//        //        endPoint.AddressFamily,
//        //        SocketType.Stream,
//        //        ProtocolType.Tcp
//        //    );

//        //    await _socket.ConnectAsync(endPoint);

//        //    _receiveTask = Task.Run(() => ReceiveLoopAsync(_cts.Token));
//        //}


//        //public async Task StopAsync()
//        //{
//        //    if (_socket == null)
//        //        return;

//        //    _cts?.Cancel();

//        //    try
//        //    {
//        //        _socket.Shutdown(SocketShutdown.Both);
//        //    }
//        //    catch
//        //    {
//        //        // ignore
//        //    }

//        //    _socket.Close();
//        //    _socket.Dispose();
//        //    _socket = null;

//        //    if (_receiveTask != null)
//        //        await _receiveTask;
//        //}

//        //private async Task ReceiveLoopAsync(CancellationToken token)
//        //{

//        //    var buffer = new byte[4096];
//        //    //       var buffer = new byte[1_024];  

//        //    try
//        //    {

//        //        while (!token.IsCancellationRequested)
//        //        {

//        //            //            // Receive message.


//        //            int bytesRead = await _socket!
//        //                .ReceiveAsync(buffer, SocketFlags.None, token);

//        //            if (bytesRead == 0)
//        //                break; // remote closed

//        //            byte[] received = new byte[bytesRead];
//        //          //  Buffer.BlockCopy(buffer, 0, received, 0, bytesRead);


//        //      //    var received = await handler.ReceiveAsync(buffer, SocketFlags.None);
//        //            var response = Encoding.UTF8.GetString(buffer, 0, received.Length);

//        //            var eom = "<|EOM|>";
//        //            if (response.IndexOf(eom) > -1 /* is end of message */)
//        //            {
//        //                WeakReferenceMessenger.Default
//        //                        .Send(new TCPReceiveMessage($"Socket server received message: {response}"));


//        //                var ackMessage = "<|ACK|>";
//        //                var echoBytes = Encoding.UTF8.GetBytes(ackMessage);
//        //                await _socket.SendAsync(echoBytes, 0);

//        //                WeakReferenceMessenger.Default
//        //        .Send(new TCPReceiveMessage($"Socket server sent acknowledgment: {ackMessage}"));


//        //            }
//        //            }
//        //    }
//        //    catch (OperationCanceledException)
//        //    {
//        //        // normal shutdown
//        //    }
//        //    catch (Exception ex)
//        //    {
//        //        WeakReferenceMessenger.Default.Send(ex);
//        //    }
//        //}

//        //public async ValueTask DisposeAsync()
//        //{
//        //    await StopAsync();
//        //    _cts?.Dispose();
//        //}


//        ////    // Handles IPv4 and IPv6 notation.
//        //public static IPEndPoint CreateIPEndPointFromString(string endPoint)
//        //{
//        //    string[] ep = endPoint.Split(':');
//        //    if (ep.Length < 2) throw new FormatException("Invalid Endpoint Format");
//        //    IPAddress ip;
//        //    if (ep.Length > 2)
//        //    {
//        //        if (!IPAddress.TryParse(string.Join(":", ep, 0, ep.Length - 1), out ip))
//        //        {
//        //            throw new FormatException("Invalid IP Address");
//        //        }
//        //    }
//        //    else
//        //    {
//        //        if (!IPAddress.TryParse(ep[0], out ip))
//        //        {
//        //            throw new FormatException("Invalid IP Address");
//        //        }
//        //    }
//        //    int numericPort;
//        //    if (!int.TryParse(ep[ep.Length - 1], NumberStyles.None, NumberFormatInfo.CurrentInfo, out numericPort))
//        //    {
//        //        throw new FormatException("Invalid Port");
//        //    }
//        //    if (numericPort < 0 || numericPort > 65535)
//        //    {
//        //        throw new FormatException("Port Number Out of Range");
//        //    }
//        //    return new IPEndPoint(ip, numericPort);
//        //}



//    //}









//        //public class TCPService
//        //{

//        //    public string LastError { get; set; } = string.Empty;
//        //    public IPEndPoint IpEndPoint { get; set; }

//        //    private CancellationTokenSource cancellationTokenSource;

//        //    private Socket _client;



//        //    public async Task StartTestIPAddressAndPort(string IPAddress, string Port)
//        //    {
//        //        IPEndPoint ipEndPoint = CreateIPEndPointFromString(IPAddress + ":" + Port);
//        //        await TestStart(ipEndPoint);

//        //    }


//        //    public async Task TestStart(IPEndPoint ipEndPoint)
//        //    {
//        //        _client = new(
//        //        ipEndPoint.AddressFamily,
//        //        SocketType.Stream,
//        //        ProtocolType.Tcp);
//        //        await _client.ConnectAsync(ipEndPoint);
//        //        while (true)
//        //        {
//        //            var buffer = new byte[1_024];
//        //            var received = await _client.ReceiveAsync(buffer, SocketFlags.None);
//        //            var response = Encoding.UTF8.GetString(buffer, 0, received);
//        //            WeakReferenceMessenger.Default.Send(new TCPReceiveMessage(response));
//        //        }
//        //    }

//        //    public async Task TestSend(string message)
//        //    {
//        //        var messageBytes = Encoding.UTF8.GetBytes(message);
//        //        _ = await _client.SendAsync(messageBytes, SocketFlags.None);
//        //    }

//        //    public async Task TestStop()
//        //    {
//        //        _client.Shutdown(SocketShutdown.Both);
//        //        _client.Close();
//        //        _client?.Dispose();
//        //        _client = null;
//        //    }

//        //    public async Task StartConnection(IPEndPoint ipEndPoint)
//        //    {



//        //        using Socket client = new (
//        //        ipEndPoint.AddressFamily,
//        //        SocketType.Stream,
//        //        ProtocolType.Tcp);

//        //        await client.ConnectAsync(ipEndPoint);
//        //        while (true)
//        //        {
//        //            // Send message.
//        //            var message = "Hi friends 👋!<|EOM|>";
//        //            var messageBytes = Encoding.UTF8.GetBytes(message);
//        //            _ = await client.SendAsync(messageBytes, SocketFlags.None);
//        //            Console.WriteLine($"Socket client sent message: \"{message}\"");

//        //            // Receive ack.
//        //            var buffer = new byte[1_024];
//        //            var received = await client.ReceiveAsync(buffer, SocketFlags.None);
//        //            var response = Encoding.UTF8.GetString(buffer, 0, received);
//        //            if (response == "<|ACK|>")
//        //            {
//        //                Console.WriteLine(
//        //                    $"Socket client received acknowledgment: \"{response}\"");
//        //                break;
//        //            }
//        //            // Sample output:
//        //            //     Socket client sent message: "Hi friends 👋!<|EOM|>"
//        //            //     Socket client received acknowledgment: "<|ACK|>"
//        //        }

//        //    }

//        //    public async Task SendMessage(string message)
//        //    {
//        //        var messageBytes = Encoding.UTF8.GetBytes(message);
//        //        _ = await _client.SendAsync(messageBytes, SocketFlags.None);
//        //    }


//        //    public async Task StartListenerFromIPAddressAndPort(string IPAddress, string Port)
//        //    {
//        //        IPEndPoint ipEndPoint = CreateIPEndPointFromString(IPAddress + ":" + Port);
//        //        await StartListenerFromIPEndPoint(ipEndPoint);

//        //    }


//        //    public async Task StartListenerFromIPEndPoint(IPEndPoint ipEndPoint)
//        //    {
//        //        TcpListener listener = new(ipEndPoint);

//        //        try
//        //        {
//        //            listener.Start();

//        //            cancellationTokenSource = new CancellationTokenSource();
//        //            using TcpClient handler = await listener.AcceptTcpClientAsync(cancellationTokenSource.Token);
//        //            await using NetworkStream stream = handler.GetStream();

//        //            //var message = $"📅 {DateTime.Now} 🕛";
//        //            //var dateTimeBytes = Encoding.UTF8.GetBytes(message);
//        //            //await stream.WriteAsync(dateTimeBytes);

//        //            //WeakReferenceMessenger.Default.Send(new TCPReceiveMessage(message));



//        //        }
//        //        catch (OperationCanceledException)
//        //        {
//        //            Console.WriteLine("Listener operation was canceled.");
//        //        }
//        //        catch (Exception ex)
//        //        {
//        //            LastError = ex.Message;
//        //            Console.WriteLine($"An error occurred: {ex.Message}");
//        //        }
//        //        finally
//        //        {

//        //            listener.Stop();
//        //        }

//        //    }


//        //    public async Task StartListenerFromHostName(string HostAddress, string Port)
//        //    {
//        //        IPEndPoint ipEndPoint = CreateIPEndPointFromString(HostAddress + ":" + Port);
//        //        await StartListenerFromIPEndPoint(ipEndPoint);

//        //    }

//        //    public void StopListener()
//        //    {
//        //        cancellationTokenSource.Cancel();
//        //    }

//        //    public async Task SendMessageFromIPEndPoint(IPEndPoint endPoint, string message)
//        //    {
//        //        //https://learn.microsoft.com/en-us/dotnet/fundamentals/networking/sockets/socket-services
//        //        using Socket client = new(
//        //            endPoint.AddressFamily,
//        //            SocketType.Stream,
//        //            ProtocolType.Tcp);

//        //        await client.ConnectAsync(endPoint);

//        //        // Send message.
//        //        var messageBytes = Encoding.UTF8.GetBytes(message);
//        //        _ = await client.SendAsync(messageBytes, SocketFlags.None);
//        //        Console.WriteLine($"Socket client sent message: \"{message}\"");
//        //        client.Shutdown(SocketShutdown.Both);
//        //    }

//        //    public async Task SendMessageFromIPAddressAndPort(string IPAddress, string Port, string message)
//        //    {
//        //        IPEndPoint ipEndPoint = CreateIPEndPointFromString(IPAddress + ":" + Port);
//        //        await SendMessageFromIPEndPoint(ipEndPoint, message);
//        //    }

//            // Handles IPv4 and IPv6 notation.
//            public static IPEndPoint CreateIPEndPointFromString(string endPoint)
//        {
//            string[] ep = endPoint.Split(':');
//            if (ep.Length < 2) throw new FormatException("Invalid Endpoint Format");
//            IPAddress ip;
//            if (ep.Length > 2)
//            {
//                if (!IPAddress.TryParse(string.Join(":", ep, 0, ep.Length - 1), out ip))
//                {
//                    throw new FormatException("Invalid IP Address");
//                }
//            }
//            else
//            {
//                if (!IPAddress.TryParse(ep[0], out ip))
//                {
//                    throw new FormatException("Invalid IP Address");
//                }
//            }
//            int numericPort;
//            if (!int.TryParse(ep[ep.Length - 1], NumberStyles.None, NumberFormatInfo.CurrentInfo, out numericPort))
//            {
//                throw new FormatException("Invalid Port");
//            }
//            if (numericPort < 0 || numericPort > 65535)
//            {
//                throw new FormatException("Port Number Out of Range");
//            }
//            return new IPEndPoint(ip, numericPort);
//        }

//        public static async Task<IPEndPoint> CreateIPEndPointFromHostName(string hostName, string port)
//        {
//            int numericPort;
//            if (!int.TryParse(port, NumberStyles.None, NumberFormatInfo.CurrentInfo, out numericPort))
//            {
//                throw new FormatException("Invalid Port Format");
//            }
//            if (numericPort < 0 || numericPort > 65535)
//            {
//                throw new FormatException("Port Number Out of Range");
//            }
//            IPHostEntry ipHostInfo = await Dns.GetHostEntryAsync(hostName);
//            IPAddress ipAddress = ipHostInfo.AddressList[0];
//            return new(ipAddress, numericPort);

//        }
//    }
//}
