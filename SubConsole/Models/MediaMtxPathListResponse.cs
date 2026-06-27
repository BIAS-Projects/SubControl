
using System;
using System.Collections.Generic;
using System.Text;
using System.Text.Json.Serialization;

namespace SubConsole.Models
{
    public sealed class MediaMtxPathListResponse
    {
        public List<MediaMtxPathItem> Items { get; set; } = new();
    }

    public sealed class MediaMtxPathItem
    {
        public string Name { get; set; } = string.Empty;

        // Optional fields depending on MTX version/config
        //public int? SourceReady { get; set; }
        public int? SourceReady { get; set; }

       // public int? Readers { get; set; }
        public List<object> Readers { get; set; } = new();


        [JsonPropertyName("ready")]
        public bool Ready { get; set; }


        [JsonPropertyName("available")]
        public bool Available { get; set; }
    }
}
