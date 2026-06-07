namespace SubControlMAUI.Services;

using SQLitePCL;
using SubConsole.Models;
using SubControlMAUI.Models;
using System.ComponentModel.Design;
using System.Diagnostics;
using System.Text.Json;
using static System.Net.Mime.MediaTypeNames;

/// <summary>
/// Handles two TCP command patterns used across all ViewModels:
///
///   1. Request / Response  – we send a command; the server echoes it back with a
///      CommandResponse JSON payload on the SAME function channel.
///      Use: <see cref="SendAndWaitAsync"/>
///
///   2. Push Confirm – we send a command on one channel; the device confirms by
///      pushing an unsolicited message on the SAME or a DIFFERENT channel that
///      satisfies a caller-supplied predicate.
///      Use: <see cref="SendAndWaitForPushAsync"/>
///
/// Register one instance per-ViewModel (or share a singleton) and call
/// <see cref="HandleIncoming"/> from the ViewModel's TcpDataReceivedMessage
/// handler.  No messenger coupling inside the service keeps it testable.
/// </summary>
public sealed class CommandDispatcherService
{
    public string Owner { get; set; }

    // -------------------------------------------------------------------------
    // Internal pending-operation descriptors
    // -------------------------------------------------------------------------

    /// <summary>Represents a pending Request/Response operation.</summary>
    private sealed class PendingResponse
    {
        public string Function { get; }
        public string Command { get; }
        public TaskCompletionSource<bool> Tcs { get; }

        public PendingResponse(string function, string command)
        {
            Function = function;
            Command = command;
            Tcs = new TaskCompletionSource<bool>(
                TaskCreationOptions.RunContinuationsAsynchronously);
        }
    }

    /// <summary>Represents a pending Push-Confirm operation.</summary>
    private sealed class PendingPush
    {
        public string Function { get; }
        public Func<string, bool> Predicate { get; }
        public TaskCompletionSource<bool> Tcs { get; }

        public PendingPush(string function, Func<string, bool> predicate)
        {
            Function = function;
            Predicate = predicate;
            Tcs = new TaskCompletionSource<bool>(
                TaskCreationOptions.RunContinuationsAsynchronously);
        }
    }

    // -------------------------------------------------------------------------
    // State (one slot each; only one outstanding operation per service instance)
    // -------------------------------------------------------------------------

    private PendingResponse? _pendingResponse;
    private PendingPush? _pendingPush;

    private readonly TcpSocketService _tcp;
    private readonly JsonSerializerOptions _jsonOpts =
        new() { PropertyNameCaseInsensitive = true };

    public CommandDispatcherService(TcpSocketService tcp)
    {
        _tcp = tcp;
    }

    // -------------------------------------------------------------------------
    // Public API
    // -------------------------------------------------------------------------

    /// <summary>
    /// Sends a command and waits for the server to echo it back on the same
    /// <paramref name="feature"/> channel with a <c>CommandResponse</c> JSON body
    /// whose <c>Ok</c> field is <c>true</c>.
    /// </summary>
    /// <param name="feature">Function/feature name used in the TCP envelope.</param>
    /// <param name="command">Command string.</param>
    /// <param name="data">Payload (may be empty string).</param>
    /// <param name="timeout">How long to wait for the response.</param>
    /// <returns><c>true</c> if the server acknowledged with Ok == true.</returns>
    public async Task<bool> SendAndWaitAsync(
        string feature,
        string command,
        string data,
        TimeSpan timeout)
    {
        var op = new PendingResponse(feature, command);
        Interlocked.Exchange(ref _pendingResponse, op);

        try
        {
            bool sent = await _tcp.SendCommandAsync(
                new TCPMessageBody<string>(feature, command, data),
                CancellationToken.None);

            if (!sent)
                return false;
            Debug.WriteLine($"SendAndWaitAsync sent {feature} {command} {data} for {Owner}");
            var completed = await Task.WhenAny(op.Tcs.Task, Task.Delay(timeout));
            return completed == op.Tcs.Task && op.Tcs.Task.Result;
        }
        finally
        {
            // Clear only if this operation is still the active one
            Interlocked.CompareExchange(ref _pendingResponse, null, op);
        }
    }

    /// <summary>
    /// Sends a command on <paramref name="sendFeature"/> and waits for an
    /// unsolicited push message on <paramref name="confirmFunction"/> whose
    /// data string satisfies <paramref name="confirmPredicate"/>.
    /// </summary>
    /// <param name="sendFeature">Feature used to send the command.</param>
    /// <param name="sendCommand">Command string.</param>
    /// <param name="sendData">Payload.</param>
    /// <param name="confirmFunction">
    ///   Function name of the PUSH channel to listen on.
    ///   May be the same as <paramref name="sendFeature"/> or different
    ///   (e.g. "TOM Output" when sending on "TOM Input").
    /// </param>
    /// <param name="confirmPredicate">
    ///   Returns <c>true</c> when the received data string confirms the operation.
    /// </param>
    /// <param name="timeout">How long to wait for the push.</param>
    /// <returns><c>true</c> if the push arrived and the predicate matched.</returns>
    public async Task<bool> SendAndWaitForPushAsync(
        string sendFeature,
        string sendCommand,
        string sendData,
        string confirmFunction,
        Func<string, bool> confirmPredicate,
        TimeSpan timeout)
    {
        var op = new PendingPush(confirmFunction, confirmPredicate);
        Interlocked.Exchange(ref _pendingPush, op);

        try
        {
            bool sent = await _tcp.SendCommandAsync(
                new TCPMessageBody<string>(sendFeature, sendCommand, sendData),
                CancellationToken.None);

            if (!sent)
                return false;
            Debug.WriteLine($"SendAndWaitForPushAsync sent {sendFeature} {sendCommand} {sendData} for {Owner}");
            var completed = await Task.WhenAny(op.Tcs.Task, Task.Delay(timeout));
            return completed == op.Tcs.Task && op.Tcs.Task.Result;
        }
        finally
        {
            Interlocked.CompareExchange(ref _pendingPush, null, op);
        }
    }

    /// <summary>
    /// Call this from the ViewModel's <c>TcpDataReceivedMessage</c> handler for
    /// every incoming message.  The service will route it to whichever pending
    /// operation (if any) it satisfies.
    /// </summary>
    /// <param name="function">Value of <c>msg.Value.Function</c>.</param>
    /// <param name="command">Value of <c>msg.Value.Command</c>.</param>
    /// <param name="data">Value of <c>msg.Value.Data</c>.</param>
    public void HandleIncoming(string function, string command, string? data)
    {
        Debug.WriteLine($"HandleIncoming {function} {command} {data} for  {Owner}");
        if (!command.Equals(Feature.PushNotification))
        {
            TryResolveResponse(function, command, data);
        }
        else
        {
            TryResolvePush(function, data);
        }
    }

    // -------------------------------------------------------------------------
    // Internal resolution helpers
    // -------------------------------------------------------------------------

    private void TryResolveResponse(string function, string command, string? data)
    {
        Debug.WriteLine($"TryResolveResponse {function} {command} {data} for  {Owner}");
        // Atomically read without clearing — we only clear after resolution
        var op = Volatile.Read(ref _pendingResponse);
        if (op is null) return;

        if (!op.Function.Equals(function, StringComparison.OrdinalIgnoreCase)) return;
        if (!op.Command.Equals(command, StringComparison.OrdinalIgnoreCase)) return;

        bool ok = false;
        try
        {
            var response = JsonSerializer.Deserialize<CommandResponse>(
                data ?? string.Empty, _jsonOpts);
            ok = response?.Ok == true;
        }
        catch { /* malformed JSON → ok stays false */ }

        Debug.WriteLine($"TryResolveResponse {data} is ok  for {Owner}");
        op.Tcs.TrySetResult(ok);
    }

    private void TryResolvePush(string function, string? data)
    {
        Debug.WriteLine($"TryResolveResponse {function} {data} for {Owner}");
        var op = Volatile.Read(ref _pendingPush);
        Debug.WriteLine($"TryResolvePush pending push {op}");
        if (op is null) return;

        if (!op.Function.Equals(function, StringComparison.OrdinalIgnoreCase)) return;

        string raw = data?.Trim() ?? string.Empty;
        if (string.IsNullOrEmpty(raw)) return;

        if (op.Predicate(raw))
        {
            Debug.WriteLine($"TryResolvePush op.Predicate is true {raw} for {Owner}");
            op.Tcs.TrySetResult(true);
        }
    }
}