using System;
using System.Reflection;
using Gst;

namespace SubConsole.Helpers
{
    public static class GstMessageExtensions
    {
        private delegate void ParseErrorNewDelegate(Message msg, out GLib.GException error, out string debug);
        private delegate void ParseErrorOldDelegate(Message msg, out IntPtr error, out string debug);

        private delegate void ParseWarningNewDelegate(Message msg, out GLib.GException error, out string debug);
        private delegate void ParseWarningOldDelegate(Message msg, out IntPtr error, out string debug);

        private static readonly ParseErrorNewDelegate? _parseErrorNew;
        private static readonly ParseErrorOldDelegate? _parseErrorOld;

        private static readonly ParseWarningNewDelegate? _parseWarningNew;
        private static readonly ParseWarningOldDelegate? _parseWarningOld;

        // Static constructor runs once
        static GstMessageExtensions()
        {
            var msgType = typeof(Message);

            // Detect ParseError signature
            foreach (var method in msgType.GetMethods())
            {
                if (method.Name != "ParseError") continue;

                var parameters = method.GetParameters();
                if (parameters.Length != 2) continue;

                if (parameters[0].ParameterType == typeof(GLib.GException).MakeByRefType())
                {
                    _parseErrorNew = (ParseErrorNewDelegate)
                        Delegate.CreateDelegate(typeof(ParseErrorNewDelegate), method);
                }
                else if (parameters[0].ParameterType == typeof(IntPtr).MakeByRefType())
                {
                    _parseErrorOld = (ParseErrorOldDelegate)
                        Delegate.CreateDelegate(typeof(ParseErrorOldDelegate), method);
                }
            }

            // Detect ParseWarning signature
            foreach (var method in msgType.GetMethods())
            {
                if (method.Name != "ParseWarning") continue;

                var parameters = method.GetParameters();
                if (parameters.Length != 2) continue;

                if (parameters[0].ParameterType == typeof(GLib.GException).MakeByRefType())
                {
                    _parseWarningNew = (ParseWarningNewDelegate)
                        Delegate.CreateDelegate(typeof(ParseWarningNewDelegate), method);
                }
                else if (parameters[0].ParameterType == typeof(IntPtr).MakeByRefType())
                {
                    _parseWarningOld = (ParseWarningOldDelegate)
                        Delegate.CreateDelegate(typeof(ParseWarningOldDelegate), method);
                }
            }
        }

        // ---------------------------------------------------------------------
        // Public helpers
        // ---------------------------------------------------------------------

        public static (string Message, string Debug) GetError(Message msg)
        {
            if (_parseErrorNew != null)
            {
                _parseErrorNew(msg, out var err, out var debug);
                return (err?.Message ?? "Unknown error", debug);
            }

            if (_parseErrorOld != null)
            {
                _parseErrorOld(msg, out var ptr, out var debug);

                if (ptr != IntPtr.Zero)
                {
                    var err = new GLib.GException(ptr);
                    return (err.Message, debug);
                }

                return ("Unknown error", debug);
            }

            return ("ParseError not available", string.Empty);
        }

        public static (string Message, string Debug) GetWarning(Message msg)
        {
            if (_parseWarningNew != null)
            {
                _parseWarningNew(msg, out var warn, out var debug);
                return (warn?.Message ?? "Unknown warning", debug);
            }

            if (_parseWarningOld != null)
            {
                _parseWarningOld(msg, out var ptr, out var debug);

                if (ptr != IntPtr.Zero)
                {
                    var warn = new GLib.GException(ptr);
                    return (warn.Message, debug);
                }

                return ("Unknown warning", debug);
            }

            return ("ParseWarning not available", string.Empty);
        }
    }
}