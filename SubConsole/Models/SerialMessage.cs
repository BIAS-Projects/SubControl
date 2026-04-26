using System;
using System.Collections.Generic;
using System.Text;

namespace SubConsole.Models
{
    /// <summary>
    /// Returned to callers on every read from a serial port.
    /// Carries the raw data plus enough context to route the message.
    /// </summary>
    public sealed class SerialMessage
    {
        /// <summary>Logical function name resolved via the device registry.</summary>
        public required string FunctionName { get; init; }

        /// <summary>OS port path this message arrived on.</summary>
        public required string PortPath { get; init; }

        /// <summary>Raw byte payload — never null, may be empty.</summary>
        public required byte[] Payload { get; init; }

        /// <summary>
        /// Decoded text payload, populated for text-mode workers.
        /// Null for binary-mode workers.
        /// </summary>
        public string? Text { get; init; }

        public DateTimeOffset Timestamp { get; } = DateTimeOffset.UtcNow;
    }

}
