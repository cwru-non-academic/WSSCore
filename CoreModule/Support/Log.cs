using System;
using System.Diagnostics;
namespace Wss.CoreModule
{
    /// <summary>
    /// Lightweight, host-agnostic logger.
    /// - Works in plain .NET via Console/Debug.
    /// - Detects Unity at runtime via reflection and forwards to UnityEngine.Debug.
    /// - Exposes a pluggable sink/event so hosts can capture logs.
    /// </summary>
    public static class Log
    {
        /// <summary>Log severity.</summary>
        public enum LogLevel
        {
            /// <summary>Informational message.</summary>
            Info,
            /// <summary>Warning message.</summary>
            Warn,
            /// <summary>Error message.</summary>
            Error
        }

        /// <summary>
        /// Raised for each message written via this logger, after the sink is invoked.
        /// </summary>
        public static event Action<LogLevel, string> Message;

        private static readonly object _gate = new object();
        private static Action<LogLevel, string> _sink = CreateDefaultSink();

        /// <summary>Replace the log sink with a custom receiver (e.g., Unity adapter or Python callback).</summary>
        public static void SetSink(Action<LogLevel, string> sink)
        {
            lock (_gate) { _sink = sink ?? CreateDefaultSink(); }
        }

        /// <summary>Reset to the default behavior (Unity if present, otherwise Console/Debug).</summary>
        public static void ResetSink()
        {
            lock (_gate) { _sink = CreateDefaultSink(); }
        }

        /// <summary>Writes an informational message.</summary>
        public static void Info(string message)  => Write(LogLevel.Info,  message);
        /// <summary>Writes a warning message.</summary>
        public static void Warn(string message)  => Write(LogLevel.Warn,  message);
        /// <summary>Writes an error message.</summary>
        public static void Error(string message) => Write(LogLevel.Error, message);

        /// <summary>
        /// Writes an exception as an error message.
        /// </summary>
        /// <param name="ex">Exception to log.</param>
        /// <param name="prefix">Optional prefix to prepend to the exception string.</param>
        public static void Error(Exception ex, string prefix = null)
        {
            var msg = prefix == null ? ex?.ToString() : (prefix + ": " + ex);
            Write(LogLevel.Error, msg);
        }

        private static void Write(LogLevel level, string message)
        {
            Action<LogLevel, string> sink;
            lock (_gate) { sink = _sink; }
            try { sink(level, message); } catch { /* ignore sink failures */ }
            try { Message?.Invoke(level, message); } catch { /* ignore subscriber failures */ }
        }

        private static Action<LogLevel, string> CreateDefaultSink()
        {
            // Try to bind UnityEngine.Debug via reflection (no compile-time dependency)
            try
            {
                var unityDebug = Type.GetType("UnityEngine.Debug, UnityEngine.CoreModule")
                                ?? Type.GetType("UnityEngine.Debug, UnityEngine");
                if (unityDebug != null)
                {
                    var log   = unityDebug.GetMethod("Log", new[] { typeof(object) });
                    var warn  = unityDebug.GetMethod("LogWarning", new[] { typeof(object) });
                    var error = unityDebug.GetMethod("LogError", new[] { typeof(object) });
                    if (log != null && warn != null && error != null)
                    {
                        return (lvl, msg) =>
                        {
                            var arg = (object)msg;
                            try
                            {
                                switch (lvl)
                                {
                                    case LogLevel.Info:  log.Invoke(null, new[] { arg });   break;
                                    case LogLevel.Warn:  warn.Invoke(null, new[] { arg });  break;
                                    case LogLevel.Error: error.Invoke(null, new[] { arg }); break;
                                }
                            }
                            catch { DefaultConsoleSink(lvl, msg); }
                        };
                    }
                }
            }
            catch { /* ignore reflection failures and fall back */ }

            // Fallback: Console + Debug
            return DefaultConsoleSink;
        }

        private static void DefaultConsoleSink(LogLevel level, string message)
        {
            var line = $"[{DateTime.Now:HH:mm:ss}] {level.ToString().ToUpperInvariant()}: {message}";
            try { Debug.WriteLine(line); } catch { }
            try { Console.WriteLine(line); } catch { }
        }
    }
}
