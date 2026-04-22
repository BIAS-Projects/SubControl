using SubConsole.Models;
using System;
using System.Collections.Generic;
using System.Net.Sockets;
using System.Text;

namespace SubConsole.Services.TCP.Commands
{
    public class StopTomAllCommand : TcpCommandBase
    {
     //   private readonly MyService _service;
        public override string CommandName => "STOP TOM ALL";


        //    public StopTomAllCommand(MyService service) => _service = service;

        //protected override Task<OperationResult> RunAsync( CancellationToken token)
        //    => _service.TOMStopAllSystems(token);



        protected override async Task<OperationResultWithValue<TCPMessageBody>> RunAsync( CancellationToken token)
        {
            Console.WriteLine("STOP TOM ALL");
            return OperationResultWithValue<TCPMessageBody>.Success(null);
        }
    }
}
