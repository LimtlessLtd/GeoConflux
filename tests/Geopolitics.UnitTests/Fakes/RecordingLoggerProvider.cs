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
/// <para>
/// That includes the exception. A logger is handed the exception separately from the message, and
/// every real sink writes both — so a recorder that kept only the formatted message was blind to the
/// most likely way a credential reaches a log file, which is a transport failure whose message quotes
/// the request that failed. The assertions that a secret never appears here depend on this.
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

            var line = $"{category} [{logLevel}] {formatter(state, exception)}";
            lines.Enqueue(exception is null ? line : string.Join('\n', line, exception));
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
