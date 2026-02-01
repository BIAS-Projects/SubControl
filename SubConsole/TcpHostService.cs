using System;
using System.Text;
using System.Net;
using System.Net.Sockets;
using System.Collections.Concurrent;

namespace SubConsole
{
    public sealed class TcpHostService : IDisposable
    {
        private readonly TcpListener _listener;
        private readonly CancellationTokenSource _cts = new();

        private readonly ConcurrentDictionary<TcpClient, ClientState> _clients = new();

        public event Action<TcpClient, string>? MessageReceived;
        public event Action<TcpClient>? ClientConnected;
        public event Action<TcpClient>? ClientDisconnected;

        public TcpHostService(int port)
        {
            _listener = new TcpListener(IPAddress.Any, port);
        }

        // ---------------- START ----------------

        public async Task StartAsync()
        {
            _listener.Start();
            Console.WriteLine("TCP Host started.");

            while (!_cts.IsCancellationRequested)
            {
                var client = await _listener.AcceptTcpClientAsync(_cts.Token);
                Console.WriteLine("Client connected.");

                var state = new ClientState(client);
                _clients[client] = state;

                ClientConnected?.Invoke(client);

                _ = Task.Run(() => HandleClientAsync(state));
            }
        }

        // ---------------- CLIENT HANDLER ----------------

        private async Task HandleClientAsync(ClientState state)
        {
            var client = state.Client;
            var stream = client.GetStream();
            var buffer = new byte[4096];

            try
            {
                while (!_cts.IsCancellationRequested)
                {
                    int bytesRead = await stream.ReadAsync(buffer, _cts.Token);
                    if (bytesRead == 0)
                        break;

                    var text = Encoding.UTF8.GetString(buffer, 0, bytesRead);
                    ProcessIncomingText(state, text);
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                Console.WriteLine($"Client error: {ex.Message}");
            }
            finally
            {
                CleanupClient(client);
            }
        }

        // ---------------- MESSAGE REASSEMBLY ----------------

        private void ProcessIncomingText(ClientState state, string text)
        {
            state.Buffer.Append(text);

            while (true)
            {
                var full = state.Buffer.ToString();
                var idx = full.IndexOf(TcpProtocol.EOM, StringComparison.Ordinal);

                if (idx < 0)
                    return;

                var message = full[..idx];
                state.Buffer.Remove(0, idx + TcpProtocol.EOM.Length);

                HandleCompleteMessage(state, message);
            }
        }

        // ---------------- PROTOCOL HANDLER ----------------

        private async void HandleCompleteMessage(ClientState state, string message)
        {
            // Ignore ACKs
            if (message == TcpProtocol.ACK)
                return;

            // Always ACK
            await SendRawAsync(state.Client, TcpProtocol.ACK + TcpProtocol.EOM);

            // ---- COMMANDS ----

            if (message == "START")
            {
                StartStreaming(state);
                Console.WriteLine($"Cutter started");
                return;
            }

            if (message == "STOP")
            {
                StopStreaming(state);
                Console.WriteLine($"Cutter stopped");
                return;
            }

            if (message.StartsWith("SPEED ", StringComparison.Ordinal))
            {
                if (int.TryParse(message[6..], out int speed) && speed > -1)
                {
                    state.CutterSpeed = speed.ToString();
                    Console.WriteLine($"Speed set to {speed} ms");
                    //   state.IntervalMs = speed;
                    //   Console.WriteLine($"Speed set to {speed} ms");
                }
                return;
            }

            // ---- APPLICATION MESSAGE ----
            MessageReceived?.Invoke(state.Client, message);
        }

        // ---------------- STREAMING ----------------

        private void StartStreaming(ClientState state)
        {
            if (state.Streaming)
                return;

            state.Streaming = true;
            state.StreamCts = new CancellationTokenSource();

            state.StreamTask = Task.Run(async () =>
            {
                int value = 1;

                try
                {
                    while (!state.StreamCts.Token.IsCancellationRequested)
                    {
                        //NEEDS TO BE CHANGED TO READ CURRENT

                        await SendAsync(state.Client, "CURRENT " + state.CutterSpeed);





                        await Task.Delay(state.IntervalMs, state.StreamCts.Token);
                    }
                }
                catch (OperationCanceledException) { }
            });
        }

        private void StopStreaming(ClientState state)
        {
            if (!state.Streaming)
                return;

            state.Streaming = false;
            state.StreamCts?.Cancel();
        }

        // ---------------- SEND ----------------

        public async Task SendAsync(TcpClient client, string message)
        {
            var framed = message + TcpProtocol.EOM;
            await SendRawAsync(client, framed);
        }

        private static async Task SendRawAsync(TcpClient client, string text)
        {
            if (!client.Connected)
                return;

            var bytes = Encoding.UTF8.GetBytes(text);
            await client.GetStream().WriteAsync(bytes);
        }

        // ---------------- STOP ----------------

        public async Task StopAsync()
        {
            _cts.Cancel();

            foreach (var state in _clients.Values)
                StopStreaming(state);

            foreach (var client in _clients.Keys)
                client.Close();

            _listener.Stop();
            await Task.CompletedTask;

            Console.WriteLine("TCP Host stopped.");
        }

        private void CleanupClient(TcpClient client)
        {
            Console.WriteLine($"Check if the cutter is running and shut it off if it is");
            if (_clients.TryRemove(client, out var state))
            {
                StopStreaming(state);
                client.Close();
                ClientDisconnected?.Invoke(client);
                Console.WriteLine("Client disconnected.");
            }
        }

        public void Dispose()
        {
            _ = StopAsync();
            _cts.Dispose();
        }

        // ---------------- CLIENT STATE ----------------

        private sealed class ClientState
        {
            public TcpClient Client { get; }
            public StringBuilder Buffer { get; } = new();

            public bool Streaming { get; set; }
            public int IntervalMs { get; set; } = 1000;

            public string CutterSpeed { get; set; } = "0";

            public Task? StreamTask { get; set; }
            public CancellationTokenSource? StreamCts { get; set; }

            public ClientState(TcpClient client)
            {
                Client = client;
            }
        }
    }
}
