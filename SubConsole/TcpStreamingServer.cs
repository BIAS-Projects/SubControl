using System;
using System.Collections.Generic;
using System.Text;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;

namespace SubConsole
{


    public sealed class TcpStreamingServer
    {
        private readonly int _port;
        private Socket? _listener;
        private Socket? _client;
        private CancellationTokenSource? _cts;

        private readonly StringBuilder _rxBuffer = new();

        private Task? _sendLoop;
        private CancellationTokenSource? _sendCts;

        private int _intervalMs = 1000;
        private bool _streaming;

        public TcpStreamingServer(int port)
        {
            _port = port;
        }

        public async Task StartAsync()
        {
            _cts = new CancellationTokenSource();

            _listener = new Socket(AddressFamily.InterNetworkV6, SocketType.Stream, ProtocolType.Tcp);
            _listener.DualMode = true; 
            _listener.Bind(new IPEndPoint(IPAddress.IPv6Any, _port));
            _listener.Listen(1);
            
            Console.WriteLine($"Server listening on port {_port}");

            while (!_cts.Token.IsCancellationRequested)
            {
                _client = await _listener.AcceptAsync(_cts.Token);
                Console.WriteLine("Client connected");

                await HandleClientAsync(_client, _cts.Token);
            }
        }

        private async Task HandleClientAsync(Socket client, CancellationToken token)
        {
            var buffer = new byte[4096];

            try
            {
                while (!token.IsCancellationRequested)
                {
                    int bytesRead = await client.ReceiveAsync(buffer, SocketFlags.None, token);
                    if (bytesRead == 0)
                        break;

                    var text = Encoding.UTF8.GetString(buffer, 0, bytesRead);
                    ProcessIncoming(text);
                }
            }
            catch { }
            finally
            {
                StopStreaming();
                client.Close();
                Console.WriteLine("Client disconnected");
            }
        }

        // ---------------- RECEIVE + FRAMING ----------------

        private void ProcessIncoming(string text)
        {
            _rxBuffer.Append(text);

            while (true)
            {
                var full = _rxBuffer.ToString();
                var idx = full.IndexOf(TcpProtocol.EOM, StringComparison.Ordinal);
                if (idx < 0)
                    return;

                var message = full[..idx];
                _rxBuffer.Remove(0, idx + TcpProtocol.EOM.Length);

                HandleCommand(message);
            }
        }

        // ---------------- COMMAND HANDLER ----------------

        private async void HandleCommand(string command)
        {
            // Always ACK
            await SendAsync(TcpProtocol.ACK + TcpProtocol.EOM);

            if (command == "START")
            {
                StartStreaming();
            }
            else if (command == "STOP")
            {
                StopStreaming();
            }
            else if (command.StartsWith("SPEED ", StringComparison.Ordinal))
            {
                if (int.TryParse(command[6..], out int speed) && speed > 0)
                {
                    _intervalMs = speed;
                    Console.WriteLine($"Speed set to {_intervalMs} ms");
                }
            }
        }

        // ---------------- STREAM LOOP ----------------

        private void StartStreaming()
        {
            if (_streaming)
                return;

            _streaming = true;
            _sendCts = new CancellationTokenSource();

            _sendLoop = Task.Run(async () =>
            {
                int value = 1;

                while (!_sendCts!.Token.IsCancellationRequested)
                {
                    await SendAsync($"{value}{TcpProtocol.EOM}");

                    value++;
                    if (value > 100)
                        value = 1;

                    await Task.Delay(_intervalMs, _sendCts.Token);
                }
            });
        }

        private void StopStreaming()
        {
            if (!_streaming)
                return;

            _streaming = false;
            _sendCts?.Cancel();
        }

        // ---------------- SEND ----------------

        private async Task SendAsync(string text)
        {
            if (_client?.Connected != true)
                return;

            var bytes = Encoding.UTF8.GetBytes(text);
            await _client.SendAsync(bytes, SocketFlags.None);
        }

        public void Stop()
        {
            _cts?.Cancel();
            StopStreaming();
            _client?.Close();
            _listener?.Close();
        }
    }

}
