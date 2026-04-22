using SubConsole.Models;
using SubConsole.Services.Serial;
using System;
using System.Collections.Generic;
using System.Text;

namespace SubConsole.Services
{
    internal interface IFLIRSerialWorker : ISerialWorker
    {
        Task<OperationResult> FLIRSetLUTtoWHITEHOT();

        Task<OperationResult> FLIRSetLUTtoRAINBOW();

    }
}
