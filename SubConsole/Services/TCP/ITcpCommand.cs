using SubConsole.Models;
using System;
using System.Collections.Generic;
using System.Net.Sockets;
using System.Text;

namespace SubConsole.Services.TCP
{
    public interface ITcpCommand
    {
        string CommandName { get; }

        Task<OperationResultWithValue<TCPMessageBody>> ExecuteAsync(CancellationToken token);
    }
}
