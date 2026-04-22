//using SubConsole.Models;
//using System;
//using System.Collections.Generic;
using SubConsole.Models;
using System.Net.Sockets;
using System.Text;

namespace SubConsole.Services.TCP.Commands
{
    public class StartTomAllCommand : TcpCommandBase
    {

        public StartTomAllCommand(TcpSerialCommandHandler service)
        {
            _service = service;
        }
        private readonly TcpSerialCommandHandler _service;
        public override string CommandName => "START TOM ALL";

        protected override Task<OperationResultWithValue<TCPMessageBody>> RunAsync(CancellationToken token)
        {
            throw new NotImplementedException();
        }

        //protected override async Task<OperationResultWithValue<TCPMessageBody>> RunAsync(CancellationToken token)
        //    => await _service.HandleListDevicesAsync(token);

        //protected override async Task<OperationResult> RunAsync(CancellationToken token)
        //{
        //    Console.WriteLine("START TOM ALL");
        //    return OperationResult.Success();
        //}
    }
}
