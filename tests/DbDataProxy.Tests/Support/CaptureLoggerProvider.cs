using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace DbDataProxy.Tests.Support;

public sealed record CapturedLine(string Category, LogLevel Level, string Text);

/// <summary>Collects every formatted log line, including the exception text, as a sink would receive it.</summary>
public sealed class CaptureLoggerProvider : ILoggerProvider
{
    private readonly ConcurrentQueue<CapturedLine> _lines = new();

    public IReadOnlyList<CapturedLine> Lines => [.. _lines];

    public ILogger CreateLogger(string categoryName) => new CaptureLogger(categoryName, _lines);

    public void Dispose() { }

    private sealed class CaptureLogger(string category, ConcurrentQueue<CapturedLine> lines) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            string text = formatter(state, exception);
            if (exception is not null) text += Environment.NewLine + exception;
            lines.Enqueue(new CapturedLine(category, logLevel, text));
        }
    }
}
