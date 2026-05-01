using SubConsole.Services.Helpers;
using System;
using System.Collections.Generic;
using System.Text;

namespace SubConsole.Models
{
    public record PortRemapResult(
        string? Function,
        string? OldPort,
        string? NewPort,
        PortChangeKind Kind,
        string? Error)
    {
        public bool IsNoOp => Function is null;
        public static PortRemapResult NoOp() => new(null, null, null, default, null);
    }
}
