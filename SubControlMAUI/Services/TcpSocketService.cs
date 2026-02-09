using CommunityToolkit.Mvvm.Messaging;
using SubControlMAUI.Messages;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;

namespace SubControlMAUI.Services
{


    public sealed class TcpSocketService : IDisposable
    {
        private readonly IMessenger _messenger;
        private Socket? _socket;
        private CancellationTokenSource? _cts;
        private Task? _receiveTask;
        private readonly SemaphoreSlim _sendLock = new(1, 1);

        private readonly StringBuilder _incomingBuffer = new();

        public TcpSocketService(IMessenger messenger)
        {
            _messenger = messenger;

            _messenger.Register<TcpSendRequestMessage>(this, async (_, msg) =>
            {
                await SendAsync(msg.Value);
            });


        }

        public async Task StartAsync(string host, int port)
        {
            try
            {
              //  host = "fe80::8aa2:9eff:fe8d:678f%3";

                _cts = new CancellationTokenSource();

                //_socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp)
                //{
                //    NoDelay = true
                //};

                _socket = new Socket(SocketType.Stream, ProtocolType.Tcp)
                {
                    NoDelay = true
                };

                _messenger.Send(new TcpStatusMessage("Connecting..."));

                await _socket.ConnectAsync(host, port, _cts.Token);

                _messenger.Send(new TcpStatusMessage("Connected"));

                _messenger.Send(new TcpIsConnected(true));

                _receiveTask = Task.Run(() => ReceiveLoop(_cts.Token));
            }
            catch (Exception ex)
            {
                _messenger.Send(new TcpErrorMessage(ex));
                _messenger.Send(new TcpIsConnected(false));
            }
        }

        // ---------------- RECEIVE LOOP WITH FRAMING ----------------

        private async Task ReceiveLoop(CancellationToken token)
        {
            var buffer = new byte[4096];

            try
            {
                while (!token.IsCancellationRequested)
                {
                    int bytesRead = await _socket!.ReceiveAsync(buffer, SocketFlags.None, token);

                    //if (bytesRead == 0)
                    //{
                    //    await StopAsync();
                    //    return;
                    //}

                    if (bytesRead == 0)
                    {
                        _messenger.Send(new TcpStatusMessage("Remote Closed Connection"));
                        break; // just exit the loop
                    }

                    var text = Encoding.UTF8.GetString(buffer, 0, bytesRead);

                    ProcessIncomingText(text);
                }
            }
            //catch (OperationCanceledException) { }
            //catch (Exception ex)
            //{
            //    _messenger.Send(new TcpIsConnected(false));
            //    _messenger.Send(new TcpErrorMessage(ex));
            //    await StopAsync();
            //}
            catch (OperationCanceledException)
            {
                // normal shutdown
            }
            catch (Exception ex)
{
                _messenger.Send(new TcpErrorMessage(ex));
            }
            finally
{
                _messenger.Send(new TcpIsConnected(false));
            }

        }

        // ---------------- MESSAGE REASSEMBLY ----------------

        private async Task ProcessIncomingText(string text)
        {
            _incomingBuffer.Append(text);

            while (true)
            {
                var full = _incomingBuffer.ToString();
                var idx = full.IndexOf(TcpProtocol.EOM, StringComparison.Ordinal);

                if (idx < 0)
                    return;

                var message = full[..idx];
                _incomingBuffer.Remove(0, idx + TcpProtocol.EOM.Length);

                await HandleCompleteMessage(message);
            }
        }

        // ---------------- PROTOCOL HANDLER ----------------

        private async Task HandleCompleteMessage(string message)
        {
            // If this is ACK -> ignore
            if (message == TcpProtocol.ACK)
                return;

            // Send ACK back
            await SendRawAsync(TcpProtocol.ACK + TcpProtocol.EOM);

            // Forward to ViewModel
            var data = Encoding.UTF8.GetBytes(message);
            _messenger.Send(new TcpDataReceivedMessage(data));
        }

        // ---------------- SEND ----------------

        public async Task SendAsync(byte[] data)
        {
            var text = Encoding.UTF8.GetString(data);
            var framed = text + TcpProtocol.EOM;
            await SendRawAsync(framed);
        }

        private async Task SendRawAsync(string text)
        {
            if (_socket?.Connected != true) return;

            var bytes = Encoding.UTF8.GetBytes(text);

            await _sendLock.WaitAsync();
            try
            {
                await _socket.SendAsync(bytes, SocketFlags.None);
            }
            finally
            {
                _sendLock.Release();
            }
        }

        // ---------------- STOP ----------------

        public async Task StopAsync()
        {
            try
            {
                _cts?.Cancel();

                if (_socket != null)
                {
                    if (_socket.Connected)
                        _socket.Shutdown(SocketShutdown.Both);

                    _socket.Close();
                    _socket.Dispose();
                }

                if (_receiveTask != null)
                    await _receiveTask;

                _messenger.Send(new TcpStatusMessage("Disconnected"));
                _messenger.Send(new TcpIsConnected(false));
            }
            catch { }
        }

        public void Dispose()
        {
            _ = StopAsync();
            _cts?.Dispose();
            _sendLock.Dispose();
        }



        public static bool CheckEndPointIsValid(string endPoint)
        {
            string[] ep = endPoint.Split(':');
            if (ep.Length < 2) throw new FormatException("Invalid Endpoint Format");
            IPAddress ip;
            if (ep.Length > 2)
            {
                if (!IPAddress.TryParse(string.Join(":", ep, 0, ep.Length - 1), out ip))
                {
                    throw new FormatException("Invalid IP Address");
                }
            }
            else
            {
                if (!IPAddress.TryParse(ep[0], out ip))
                {
                    throw new FormatException("Invalid IP Address");
                }
            }
            int numericPort;
            if (!int.TryParse(ep[ep.Length - 1], NumberStyles.None, NumberFormatInfo.CurrentInfo, out numericPort))
            {
                throw new FormatException("Invalid Port");
            }
            if (numericPort < 0 || numericPort > 65535)
            {
                throw new FormatException("Port Number Out of Range");
            }
            return true;
        }

    }
    public static class TcpProtocol
    {
        public const string EOM = "<|EOM|>";
        public const string ACK = "<|ACK|>";
    }



}
