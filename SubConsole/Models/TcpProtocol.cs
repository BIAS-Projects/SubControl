using System;
using System.Collections.Generic;
using System.Text;

namespace SubConsole.Models
{
    public static class TcpProtocol
    {
        public const string EOM = "<|EOM|>";
        public const string ACK = "<|ACK|>";
        public const string NACK = "<|NACK|>";
        public const string PUSH = "PUSH";          // ← add this
        public const string SEP = "|";
        public const string SuccessString = "OK";
        public const string CommandSeparatorChar = ",";
        public const int AckTimeoutMs = 5000;
    }
}
