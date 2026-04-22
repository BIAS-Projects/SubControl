using System;
using System.Collections.Generic;
using System.Text;


namespace SubControlMAUI.Models
{



    //"LIST DEVICES"    => await HandleListDevicesAsync(token),
    //"LIST REGISTERED" => await HandleListRegisteredAsync(token),
    //"REGISTER"        => await HandleRegisterAsync(parts, token),
    //"UNREGISTER"      => await HandleUnregisterAsync(parts, token),
    //"OPEN"            => await HandleOpenAsync(parts, token),
    //"CLOSE"           => await HandleCloseAsync(parts, token),
    //"WRITE"           => await HandleWriteAsync(parts, token),
    //"WRITE TEXT"      => await HandleWriteTextAsync(parts, token),
    //"DISCOVER"        => await HandleDiscoverAsync(parts, token),
    //"ASSIGN PORT"     => HandleAssignPort(parts),


    //abc123|WRITE TEXT|TOM_CONTROLLER|$PBLUTP,S,PWR,CTRL,ON,15*29\n
    public record TCPMessageBody(string CommandType, string Function, string Command);
}
