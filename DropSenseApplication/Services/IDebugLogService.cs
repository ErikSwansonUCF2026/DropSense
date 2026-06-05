using System;
using System.Collections.Generic;
using System.Text;
using System.Collections.ObjectModel;
using System.Diagnostics;

namespace DropSense.Services
{
    public record LogEntry(DateTime Timestamp, string Message);

    public interface IDebugLogService
    {
        ObservableCollection<LogEntry> Entries { get; }
        void Clear();
        void Attach();
    }

    public sealed class DebugLogService : TraceListener, IDebugLogService
    {
        private const int MaxEntries = 500;
        private readonly SynchronizationContext? _syncContext;

        public ObservableCollection<LogEntry> Entries { get; } = new();

        public DebugLogService()
        {
            // Capture sync context at construction time — don't call MainThread at write time
            _syncContext = SynchronizationContext.Current;
        }

        public void Attach()
        {
            TraceOutputOptions = TraceOptions.None;

            // Remove the default listener that appends stack traces
            Trace.Listeners.Remove("Default");
            Trace.AutoFlush = false;

            Trace.Listeners.Add(this);
        }

        public void Clear()
        {
            Post(new LogEntry(DateTime.Now, "Cleared log"));
            Entries.Clear();
        }

        public override void Write(string? message) { }

        public override void WriteLine(string? message)
        {
            if (string.IsNullOrEmpty(message)) return;

            // Take only the first line — strips any appended stack trace
            var firstLine = message.Split('\n', StringSplitOptions.RemoveEmptyEntries)[0].Trim();
            if (string.IsNullOrEmpty(firstLine)) return;

            Post(new LogEntry(DateTime.Now, firstLine));
        }

        public override void TraceEvent(TraceEventCache? cache, string source,
        TraceEventType eventType, int id, string? message)
        {
            if (string.IsNullOrEmpty(message)) return;
            var firstLine = message.Split('\n', StringSplitOptions.RemoveEmptyEntries)[0].Trim();
            Post(new LogEntry(DateTime.Now, firstLine));
        }

        public override void TraceEvent(TraceEventCache? cache, string source,
            TraceEventType eventType, int id, string? format, params object?[]? args)
        {
            if (string.IsNullOrEmpty(format)) return;
            var message = args is { Length: > 0 } ? string.Format(format, args) : format;
            Post(new LogEntry(DateTime.Now, message));
        }

        private void Post(LogEntry action)
        {
            try
            {
                if (_syncContext != null)
                    _syncContext.Post(_ => Entries.Add(action), null);
                else
                    Entries.Add(action); // no UI context yet — run inline (safe, no ObservableCollection listener yet)
            }
            catch
            {
                // Never let a log write crash the app
            }
        }
    }
}

