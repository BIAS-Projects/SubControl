using SubConsole.Models;
using System;
using System.Collections.Generic;
using System.Text;

namespace SubConsole.Services
{
    public interface ISerialWorker
    {
        bool IsOpen { get; }

        Task StartAsync(CancellationToken token);
        Task StopAsync();

        Task<OperationResult> WriteAsync(string data, CancellationToken token);
    }
}
