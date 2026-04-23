using System;
using System.Collections.Generic;
using System.Text;

namespace SubConsole.Models
{
    public record TCPMessageBody<T>(string Function, string Command, T? Data);
}
