using System;
using System.Collections.Generic;
using System.Text;

namespace SubConsole.Models
{

    public sealed class MediaMtxSettings
    {
        public const string SectionName = "MediaMtx";

        public string Host { get; init; } = "localhost";
        public int Port { get; init; } = 9997;
        public bool UseTls { get; init; } = false;

        public Uri BaseAddress =>
            new($"{(UseTls ? "https" : "http")}://{Host}:{Port}/");
    }
}
