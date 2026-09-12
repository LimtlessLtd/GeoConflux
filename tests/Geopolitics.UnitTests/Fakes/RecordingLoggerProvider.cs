using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace Geopolitics.UnitTests.Fakes;

/// <summary>
/// Captures every log line the container produces, formatted exactly as a console or file sink would
/// write it.
/// <para>
/// Formatting matters here rather than structure. A test that inspected only the message template
/// would pass while the rendered line leaked a credential through a parameter, which is precisely the
/// failure this exists to catch, so the recorded text is what a sink would actually persist.
/// </para>
/// </summary>
internal sealed class RecordingLoggerProvider : ILoggerProvider
{
    public ConcurrentQueue<string> Lines { get; } = new();

    /// <summary>Everything written, as one string, which is the form an assertion wants.</summary>
    public string Transcript => string.Join('\n', Lines);

    public ILogger CreateLogger(string categoryName) => new RecordingLogger(categoryName, Lines);

    public void Dispose()
    {
    }

    private sealed class RecordingLogger(string category, ConcurrentQueue<string> lines) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull
        {
            // Scopes carry state too, and the HTTP client's request scope carries the request URI.
            // Recording it is the difference between this test seeing what a structured sink sees
            // and seeing only half of it.
            lines.Enqueue($"{category} [scope] {state}");
            return NullScope.Instance;
        }

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            ArgumentNullException.ThrowIfNull(formatter);
            lines.Enqueue($"{category} [{logLevel}] {formatter(state, exception)}");
        }

        private sealed class NullScope : IDisposable
        {
            public static readonly NullScope Instance = new();

            public void Dispose()
            {
            }
        }
    }
}
