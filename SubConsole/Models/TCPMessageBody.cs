using System;
using System.Collections.Generic;
using System.Text;

namespace SubConsole.Models
{
    public record TCPMessageBody(string MessageID, string Function, string Command, object? Data);
}
