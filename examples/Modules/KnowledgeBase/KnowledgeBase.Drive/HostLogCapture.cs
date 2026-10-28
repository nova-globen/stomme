using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using Microsoft.Extensions.Logging;

namespace NovaGloben.Module.KnowledgeBase.Drive
{
    /// <summary>
    /// The host's warnings and errors, captured for the drive: a module's failure after a commit is LOGGED, not thrown (a
    /// meter, a notification, a bus), so the log is the only account of it - the drive prints what was captured.
    /// </summary>
    internal sealed class HostLogCapture : ILoggerProvider
    {
        private readonly ConcurrentQueue<string> _entries = new();

        public IReadOnlyList<string> Drain()
        {
            var drained = new List<string>();
            while (_entries.TryDequeue(out var entry))
            {
                drained.Add(entry);
            }

            return drained;
        }

        public ILogger CreateLogger(string categoryName) => new Logger(categoryName, _entries);

        public void Dispose()
        {
        }

        private sealed class Logger(string category, ConcurrentQueue<string> entries) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state)
                where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Warning;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            {
                if (IsEnabled(logLevel))
                {
                    entries.Enqueue($"{logLevel} {category}: {formatter(state, exception)}{(exception is null ? string.Empty : " | " + exception.GetType().Name + ": " + exception.Message)}");
                }
            }
        }
    }
}
