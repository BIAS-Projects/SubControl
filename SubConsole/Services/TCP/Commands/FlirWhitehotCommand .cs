using SubConsole.Models;
using System;
using System.Collections.Generic;
using System.Net.Sockets;
using System.Text;

namespace SubConsole.Services.TCP.Commands
{
    public class FlirWhitehotCommand : TcpCommandBase
    {
    //    private readonly MyService _service;
        public override string CommandName => "FLIR WHITEHOT";



        //     public FlirWhitehotCommand(MyService service) => _service = service;

        //protected override Task<OperationResult> RunAsync(CancellationToken token)
        //    => _service.FLIRWhitehot(token);


        protected override async Task<OperationResultWithValue<TCPMessageBody<string>>> RunAsync( CancellationToken token)
        {
            Console.WriteLine("FLIR WHITEHOT");
            return OperationResultWithValue<TCPMessageBody<string>>.Success(null);
        }
    }
}
