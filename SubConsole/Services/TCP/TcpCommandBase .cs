using SubConsole.Models;
using System;
using System.Collections.Generic;
using System.Net.Sockets;
using System.Text;

namespace SubConsole.Services.TCP
{
    public abstract class TcpCommandBase : ITcpCommand
    {
        public abstract string CommandName { get; }
        protected abstract Task<OperationResultWithValue<TCPMessageBody>> RunAsync(CancellationToken token);

        public Task<OperationResultWithValue<TCPMessageBody>> ExecuteAsync(CancellationToken token)
            => RunAsync(token);
    }
}
