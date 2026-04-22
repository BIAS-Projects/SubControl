using Microsoft.Extensions.Logging;
using SubConsole.Models;
using System;
using System.Collections.Frozen;
using System.Collections.Generic;
using System.Net.Sockets;
using System.Text;

namespace SubConsole.Services.TCP
{
    public sealed class TcpCommandInvoker : IDisposable
    {
        private readonly ILogger<TcpCommandInvoker> _logger;
        private readonly FrozenDictionary<string, ITcpCommand> _commands;
        private readonly FrozenDictionary<string, SemaphoreSlim> _locks;

        public TcpCommandInvoker(IEnumerable<ITcpCommand> commands, ILogger<TcpCommandInvoker> logger)
        {
            _logger = logger;

            var commandList = commands.ToList();

            _commands = commandList.ToFrozenDictionary(
                c => c.CommandName,
                StringComparer.OrdinalIgnoreCase);

            // One semaphore per command — prevents the same command running concurrently
            _locks = commandList.ToFrozenDictionary(
                c => c.CommandName,
                _ => new SemaphoreSlim(1, 1),
                StringComparer.OrdinalIgnoreCase);
        }

        public async Task<OperationResultWithValue<TCPMessageBody>> HandleAsync(
            TCPMessageBody command,
            CancellationToken token)
        {
            _logger.LogDebug("Handling command: {Command}", command);

            if (!_commands.TryGetValue(command.Command, out var cmd))
            {
                _logger.LogWarning("Unknown command received: '{Command}'", command.Command);
                return OperationResultWithValue<TCPMessageBody>.Failure($"Unknown command: '{command.Command}'");
            }

            var semaphore = _locks[command.Command];

            await semaphore.WaitAsync(token);
            try
            {
                return await cmd.ExecuteAsync(token);

                //return result.IsSuccess
                //    ? OperationResultWithValue<string>.Success(
                //        command + TcpProtocol.CommandSeparatorChar + TcpProtocol.SuccessString)
                //    : OperationResultWithValue<string>.Failure($"Command failed: '{command}'");
            }
            finally
            {
                semaphore.Release();
            }
        }

        public void Dispose()
        {
            foreach (var semaphore in _locks.Values)
                semaphore.Dispose();
        }
    }
}
