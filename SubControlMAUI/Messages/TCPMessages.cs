using CommunityToolkit.Mvvm.Messaging.Messages;
using System;
using System.Collections.Generic;
using System.Text;

namespace SubControlMAUI.Messages
{
    using CommunityToolkit.Mvvm.Messaging.Messages;

    public sealed class TcpDataReceivedMessage : ValueChangedMessage<byte[]>
    {
        public TcpDataReceivedMessage(byte[] value) : base(value) { }
    }

    public sealed class TcpStatusMessage : ValueChangedMessage<string>
    {
        public TcpStatusMessage(string value) : base(value) { }
    }

    public sealed class TcpErrorMessage : ValueChangedMessage<Exception>
    {
        public TcpErrorMessage(Exception value) : base(value) { }
    }

    public sealed class TcpSendRequestMessage : ValueChangedMessage<byte[]>
    {
        public TcpSendRequestMessage(byte[] value) : base(value) { }
    }

    public sealed class TcpIsConnected : ValueChangedMessage<bool>
    {
        public TcpIsConnected(bool value) : base(value) { }
    }

}
