using System;
using System.Collections.Generic;
using System.Text;

namespace SubControlMAUI.Messages
{
    using CommunityToolkit.Mvvm.Messaging.Messages;

    public sealed class FeatureUpdateMessage : ValueChangedMessage<string>
    {
        public FeatureUpdateMessage(string value) : base(value) { }
    }

}
