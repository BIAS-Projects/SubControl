using CommunityToolkit.Mvvm.ComponentModel;
using System;

namespace SubControlMAUI.ViewModels
{
    /// <summary>
    /// A single line in the SSH terminal output, tagged with its source type for colour-coding.
    /// </summary>
    public partial class TerminalLine : ObservableObject
    {
        public string Text { get; }
        public TerminalLineKind Kind { get; }
        public DateTime Timestamp { get; } = DateTime.Now;

        public TerminalLine(string text, TerminalLineKind kind = TerminalLineKind.Output)
        {
            Text = text;
            Kind = kind;
        }
    }

    public enum TerminalLineKind
    {
        Info,       // status / connection messages  → blue/grey
        Command,    // echoed user input             → orange
        Output,     // stdout from commands          → white
        Error,      // stderr / failures             → red
        Success,    // exit-code 0 confirmation       → green
        Stream,     // live tail / streaming output  → green (still used by PiViewModel until it's retired)
    }
}