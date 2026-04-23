using CommunityToolkit.Mvvm.Messaging;

using Microsoft.Extensions.Logging;
using SubControlMAUI.Messages;
using SubControlMAUI.Models;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;

namespace SubControlMAUI.Services
{
    public sealed class TcpSocketService : IDisposable
    {
        private readonly IMessenger _messenger;
        private Socket? _socket;
        private CancellationTokenSource? _cts;
        private Task? _receiveTask;
        private Task? _sendTask;

        private readonly StringBuilder _incomingBuffer = new();
        private readonly ILogger<TcpSocketService> _logger;

        // 🔥 NEW: Outgoing channel (replaces SemaphoreSlim)
        private readonly Channel<string> _outgoing =
            Channel.CreateBounded<string>(new BoundedChannelOptions(100)
            {
                FullMode = BoundedChannelFullMode.Wait
            });

        // ACK tracking
        private readonly ConcurrentDictionary<string, TaskCompletionSource<bool>> _pendingAcks = new();

        public TcpSocketService(IMessenger messenger, ILogger<TcpSocketService> logger)
        {
            _logger = logger;
            _messenger = messenger;

            _messenger.Register<TcpSendRequestMessage>(this, async (_, msg) =>
            {
                await SendCommandAsync(msg.Value);
            });
        }

        public async Task StartAsync(string host, int port)
        {
            try
            {
                _cts = new CancellationTokenSource();
                _socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };

                _messenger.Send(new TcpStatusMessage("Connecting..."));
                await _socket.ConnectAsync(host, port, _cts.Token);

                _messenger.Send(new TcpStatusMessage("Connected"));
                _messenger.Send(new TcpIsConnected(true));

                _receiveTask = Task.Run(() => ReceiveLoop(_cts.Token));

                // 🔥 NEW: start send loop
                _sendTask = Task.Run(() => SendLoop(_cts.Token));
            }
            catch (Exception ex)
            {
                _messenger.Send(new TcpErrorMessage(ex));
                _messenger.Send(new TcpIsConnected(false));
            }
        }

        // ── RECEIVE ──────────────────────────────────────────────────────────

        private async Task ReceiveLoop(CancellationToken token)
        {
            var buffer = new byte[4096];

            try
            {
                while (!token.IsCancellationRequested)
                {
                    int bytesRead = await _socket!.ReceiveAsync(buffer, SocketFlags.None, token);

                    if (bytesRead == 0)
                    {
                        _messenger.Send(new TcpStatusMessage("Remote closed connection"));
                        break;
                    }

                    ProcessIncomingText(Encoding.UTF8.GetString(buffer, 0, bytesRead));
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                _messenger.Send(new TcpErrorMessage(ex));
            }
            finally
            {
                // Cancel all pending ACKs
                foreach (var tcs in _pendingAcks.Values)
                    tcs.TrySetCanceled();

                _messenger.Send(new TcpIsConnected(false));
            }
        }

        // ── FRAMING ──────────────────────────────────────────────────────────

        private void ProcessIncomingText(string text)
        {
            _incomingBuffer.Append(text);

            while (true)
            {
                var full = _incomingBuffer.ToString();
                var idx = full.IndexOf(TcpProtocol.EOM, StringComparison.Ordinal);

                if (idx < 0) return;

                var frame = full[..idx];
                _incomingBuffer.Remove(0, idx + TcpProtocol.EOM.Length);

                HandleFrame(frame);
            }
        }

        // ── FRAME HANDLER ────────────────────────────────────────────────────

        private void HandleFrame(string frame)
        {
            if (frame.StartsWith(TcpProtocol.ACK + TcpProtocol.SEP))
            {
                var id = frame[(TcpProtocol.ACK.Length + 1)..];

                if (_pendingAcks.TryRemove(id, out var tcs))
                    tcs.TrySetResult(true);

                return;
            }

            if (frame.StartsWith(TcpProtocol.NACK + TcpProtocol.SEP))
            {
                var parts = frame.Split(TcpProtocol.SEP);

                var id = parts.Length > 3 ? parts[3] : "";
                var reason = parts.Length > 4 ? parts[4] : "NACK";

                _messenger.Send(new TcpNackMessage(id, string.Empty, reason));

                if (_pendingAcks.TryRemove(id, out var tcs))
                    tcs.TrySetException(new InvalidOperationException($"Server NACK: {reason}"));

                return;
            }

            var sepIdx = frame.IndexOf(TcpProtocol.SEP, StringComparison.Ordinal);
            var body = sepIdx >= 0 ? frame[(sepIdx + 1)..] : frame;
            try
            {
                TCPMessageBody<string> messageBody = JsonSerializer.Deserialize<TCPMessageBody<string>>(body);
                _messenger.Send(new TcpDataReceivedMessage(messageBody));
            }
            catch(Exception ex)
            {
                _messenger.Send(new TcpErrorMessage(ex));
            }

        }

        // ── SEND WITH ACK ────────────────────────────────────────────────────

        //public async Task<bool> SendAsync(byte[] data, CancellationToken callerToken = default)
        //{
        //    var text = Encoding.UTF8.GetString(data);
        //    return await SendCommandAsync(text, callerToken);
        //}

        public async Task<bool> SendCommandAsync(TCPMessageBody<string> messageBody, CancellationToken callerToken = default)
        {
            if (_socket?.Connected != true)
            {
                _messenger.Send(new TcpStatusMessage("Send failed – not connected"));
                return false;
            }

            var id = Guid.NewGuid().ToString("N")[..8];

            var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            _pendingAcks[id] = tcs;

            var command = JsonSerializer.Serialize<TCPMessageBody<string>>(messageBody);

            if (command == null)
            {
                _messenger.Send(new TcpStatusMessage($"Invalid Message {messageBody.ToString()}"));
                return false;
            }

          //  string command = $"{messageBody.CommandType}{ TcpProtocol.SEP}{messageBody.Function}{TcpProtocol.SEP}{messageBody.Command} ";

            //abc123|WRITE TEXT|TOM_CONTROLLER|$PBLUTP,S,PWR,CTRL,ON,15*29\n

            var frame = $"{id}{TcpProtocol.SEP}{command}{TcpProtocol.EOM}";

            // 🔥 enqueue instead of direct send
            await _outgoing.Writer.WriteAsync(frame, callerToken);

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(callerToken);
            var timeoutTask = Task.Delay(TcpProtocol.AckTimeoutMs, timeoutCts.Token);

            try
            {
                var winner = await Task.WhenAny(tcs.Task, timeoutTask);

                if (winner == timeoutTask)
                {
                    _pendingAcks.TryRemove(id, out _);
                    tcs.TrySetCanceled();

                    if (!callerToken.IsCancellationRequested)
                        _messenger.Send(new TcpAckTimeoutMessage(id, command));

                    return false;
                }

                await timeoutCts.CancelAsync();
                return await tcs.Task;
            }
            catch (OperationCanceledException)
            {
                _pendingAcks.TryRemove(id, out _);
                throw;
            }
            catch (InvalidOperationException ex)
            {
                _logger?.LogWarning("NACK for command '{Command}': {Reason}", command, ex.Message);
                return false;
            }
        }

        // ── SEND LOOP (NEW) ───────────────────────────────────────────────────

        private async Task SendLoop(CancellationToken token)
        {
            try
            {
                await foreach (var message in _outgoing.Reader.ReadAllAsync(token))
                {
                    if (_socket?.Connected != true)
                        continue;

                    var bytes = Encoding.UTF8.GetBytes(message);
                    await _socket.SendAsync(bytes, SocketFlags.None, token);
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                _messenger.Send(new TcpErrorMessage(ex));
            }
        }

        // ── STOP / DISPOSE ───────────────────────────────────────────────────

        public async Task StopAsync()
        {
            try
            {
                _cts?.Cancel();

                _outgoing.Writer.TryComplete();

                if (_socket != null)
                {
                    if (_socket.Connected)
                        _socket.Shutdown(SocketShutdown.Both);

                    _socket.Close();
                    _socket.Dispose();
                }

                if (_receiveTask != null) await _receiveTask;
                if (_sendTask != null) await _sendTask;

                _messenger.Send(new TcpStatusMessage("Disconnected"));
                _messenger.Send(new TcpIsConnected(false));
            }
            catch { }
        }

        public void Dispose()
        {
            _ = StopAsync();
            _cts?.Dispose();
        }

        // ── VALIDATION ────────────────────────────────────────────────────────

        public static bool CheckEndPointIsValid(string endPoint)
        {
            string[] ep = endPoint.Split(':');

            if (ep.Length < 2)
                throw new FormatException("Invalid Endpoint Format");

            if (!IPAddress.TryParse(
                    ep.Length > 2 ? string.Join(":", ep, 0, ep.Length - 1) : ep[0],
                    out _))
                throw new FormatException("Invalid IP Address");

            if (!int.TryParse(ep[^1], out int p) || p is < 0 or > 65535)
                throw new FormatException("Port out of range");

            return true;
        }
    }
}





//using CommunityToolkit.Mvvm.Messaging;
//using Microsoft.Extensions.Logging;
//using SubControlMAUI.Messages;
//using SubControlMAUI.Models;
//using System.Collections.Concurrent;
//using System.Globalization;
//using System.Net;
//using System.Net.Sockets;
//using System.Text;

//namespace SubControlMAUI.Services
//{
//    public sealed class TcpSocketService : IDisposable
//    {
//        private readonly IMessenger _messenger;
//        private Socket? _socket;
//        private CancellationTokenSource? _cts;
//        private Task? _receiveTask;
//        private readonly SemaphoreSlim _sendLock = new(1, 1);
//        private readonly StringBuilder _incomingBuffer = new();
//        private readonly ILogger<TcpSocketService> _logger;

//        // --- Correlated ACK tracking ---
//        // Key: message ID, Value: TCS that completes when server ACKs that ID
//        private readonly ConcurrentDictionary<string, TaskCompletionSource<bool>> _pendingAcks = new();

//        public TcpSocketService(IMessenger messenger, ILogger<TcpSocketService> logger)
//        {
//            _logger = logger;
//            _messenger = messenger;
//            _messenger.Register<TcpSendRequestMessage>(this, async (_, msg) =>
//            {
//                await SendAsync(msg.Value);
//            });
//        }

//        public async Task StartAsync(string host, int port)
//        {
//            try
//            {
//                _cts = new CancellationTokenSource();
//                _socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };

//                _messenger.Send(new TcpStatusMessage("Connecting..."));
//                await _socket.ConnectAsync(host, port, _cts.Token);
//                _messenger.Send(new TcpStatusMessage("Connected"));
//                _messenger.Send(new TcpIsConnected(true));

//                _receiveTask = Task.Run(() => ReceiveLoop(_cts.Token));
//            }
//            catch (Exception ex)
//            {
//                _messenger.Send(new TcpErrorMessage(ex));
//                _messenger.Send(new TcpIsConnected(false));
//            }
//        }

//        // ── RECEIVE ──────────────────────────────────────────────────────────

//        private async Task ReceiveLoop(CancellationToken token)
//        {
//            var buffer = new byte[4096];
//            try
//            {
//                while (!token.IsCancellationRequested)
//                {
//                    int bytesRead = await _socket!.ReceiveAsync(buffer, SocketFlags.None, token);
//                    if (bytesRead == 0)
//                    {
//                        _messenger.Send(new TcpStatusMessage("Remote closed connection"));
//                        break;
//                    }
//                    ProcessIncomingText(Encoding.UTF8.GetString(buffer, 0, bytesRead));
//                }
//            }
//            catch (OperationCanceledException) { }
//            catch (Exception ex) { _messenger.Send(new TcpErrorMessage(ex)); }
//            finally
//            {
//                // Fail all in-flight sends immediately instead of waiting for timeout
//                foreach (var tcs in _pendingAcks.Values)
//                    tcs.TrySetCanceled();

//                _messenger.Send(new TcpIsConnected(false));
//            }
//        }

//        // ── FRAMING ──────────────────────────────────────────────────────────

//        private void ProcessIncomingText(string text)
//        {
//            _incomingBuffer.Append(text);

//            while (true)
//            {
//                var full = _incomingBuffer.ToString();
//                var idx = full.IndexOf(TcpProtocol.EOM, StringComparison.Ordinal);
//                if (idx < 0) return;

//                var frame = full[..idx];
//                _incomingBuffer.Remove(0, idx + TcpProtocol.EOM.Length);

//                HandleFrame(frame);
//            }
//        }

//        // ── FRAME HANDLER ────────────────────────────────────────────────────
//        //
//        // Frame format coming FROM server:
//        //   ACK|<id>          – server acknowledged a command we sent
//        //   NACK|<id>|<reason> – server rejected a command we sent
//        //   <id>|<body>        – unsolicited or response message

//        private void HandleFrame(string frame)
//        {
//            if (frame.StartsWith(TcpProtocol.ACK + TcpProtocol.SEP))
//            {
//                // Server is ACK-ing one of our outbound messages
//                var id = frame[(TcpProtocol.ACK.Length + 1)..];
//                if (_pendingAcks.TryRemove(id, out var tcs))
//                    tcs.TrySetResult(true);
//                return;
//            }

//            if (frame.StartsWith(TcpProtocol.NACK + TcpProtocol.SEP))
//            {
//                var parts = frame.Split(TcpProtocol.SEP);

//                var id = parts.Length > 3 ? parts[3] : "";
//                var reason = parts.Length > 4 ? parts[4] : "NACK";

//        //        var sepIdex = frame.IndexOf(TcpProtocol.SEP, StringComparison.Ordinal);
//        //        reason = sepIdex >= 0 ? frame[(sepIdex + 3)..] : frame;

//                // Notify any global subscriber (UI, logging, telemetry) immediately
//                _messenger.Send(new TcpNackMessage(id, string.Empty, reason));

//                // Also unblock the specific caller that is awaiting this ID,
//                // so SendCommandAsync returns false rather than timing out
//                if (_pendingAcks.TryRemove(id, out var tcs))
//                    tcs.TrySetException(new InvalidOperationException($"Server NACK: {reason}"));

//                return;
//            }

//            // Regular data frame – forward to ViewModel
//            // Strip the leading ID prefix if present (id|body)
//            var sepIdx = frame.IndexOf(TcpProtocol.SEP, StringComparison.Ordinal);
//            var body = sepIdx >= 0 ? frame[(sepIdx + 1)..] : frame;

//            _messenger.Send(new TcpDataReceivedMessage(Encoding.UTF8.GetBytes(body)));
//        }

//        // ── SEND WITH ACK WAIT ────────────────────────────────────────────────
//        //
//        // Returns true  – server ACK'd within timeout
//        // Returns false – timeout or no connection (caller decides what to do)
//        // Throws        – server sent NACK (caller gets the reason)

//        public async Task<bool> SendAsync(byte[] data, CancellationToken callerToken = default)
//        {
//            var text = Encoding.UTF8.GetString(data);
//            return await SendCommandAsync(text, callerToken);
//        }

//        public async Task<bool> SendCommandAsync(string command,
//                                                  CancellationToken callerToken = default)
//        {
//            if (_socket?.Connected != true)
//            {
//                _messenger.Send(new TcpStatusMessage("Send failed – not connected"));
//                return false;
//            }

//            var id = Guid.NewGuid().ToString("N")[..8];
//            var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
//            _pendingAcks[id] = tcs;

//            var frame = $"{id}{TcpProtocol.SEP}{command}{TcpProtocol.EOM}";
//            await SendRawAsync(frame);

//            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(callerToken);
//            var timeoutTask = Task.Delay(TcpProtocol.AckTimeoutMs, timeoutCts.Token);

//            try
//            {
//                var winner = await Task.WhenAny(tcs.Task, timeoutTask);

//                if (winner == timeoutTask)
//                {
//                    // Timeout won — clean up and report
//                    _pendingAcks.TryRemove(id, out _);
//                    tcs.TrySetCanceled();

//                    if (!callerToken.IsCancellationRequested)
//                        _messenger.Send(new TcpAckTimeoutMessage(id, command));

//                    return false;
//                }

//                // ACK or NACK won — cancel the timeout task cleanly
//                await timeoutCts.CancelAsync();

//                // Await the TCS directly now to unwrap ACK (true) or NACK (exception)
//                return await tcs.Task;
//            }
//            catch (OperationCanceledException)
//            {
//                // Caller cancelled intentionally
//                _pendingAcks.TryRemove(id, out _);
//                throw;
//            }
//            catch (InvalidOperationException ex)
//            {
//                // NACK — messenger already sent in HandleFrame
//                _logger?.LogWarning("NACK for command '{Command}': {Reason}", command, ex.Message);
//                return false;
//            }
//        }

//        // ── RAW SEND ──────────────────────────────────────────────────────────

//        private async Task SendRawAsync(string text)
//        {
//            if (_socket?.Connected != true) return;
//            var bytes = Encoding.UTF8.GetBytes(text);
//            await _sendLock.WaitAsync();
//            try { await _socket.SendAsync(bytes, SocketFlags.None); }
//            finally { _sendLock.Release(); }
//        }

//        // ── STOP / DISPOSE ────────────────────────────────────────────────────

//        public async Task StopAsync()
//        {
//            try
//            {
//                _cts?.Cancel();
//                if (_socket != null)
//                {
//                    if (_socket.Connected) _socket.Shutdown(SocketShutdown.Both);
//                    _socket.Close();
//                    _socket.Dispose();
//                }
//                if (_receiveTask != null) await _receiveTask;
//                _messenger.Send(new TcpStatusMessage("Disconnected"));
//                _messenger.Send(new TcpIsConnected(false));
//            }
//            catch { }
//        }

//        public void Dispose()
//        {
//            _ = StopAsync();
//            _cts?.Dispose();
//            _sendLock.Dispose();
//        }

//        // ── VALIDATION ────────────────────────────────────────────────────────

//        public static bool CheckEndPointIsValid(string endPoint)
//        {
//            string[] ep = endPoint.Split(':');
//            if (ep.Length < 2) throw new FormatException("Invalid Endpoint Format");
//            if (!IPAddress.TryParse(
//                    ep.Length > 2 ? string.Join(":", ep, 0, ep.Length - 1) : ep[0],
//                    out _))
//                throw new FormatException("Invalid IP Address");
//            if (!int.TryParse(ep[^1], System.Globalization.NumberStyles.None,
//                    System.Globalization.NumberFormatInfo.CurrentInfo, out int p)
//                || p is < 0 or > 65535)
//                throw new FormatException("Port out of range");
//            return true;
//        }
//    }
//}