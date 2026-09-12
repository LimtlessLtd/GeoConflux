using Microsoft.Extensions.Http.Logging;
using Microsoft.Extensions.Logging;

namespace Geopolitics.Infrastructure.Sources.Providers;

/// <summary>
/// Outbound HTTP logging for the OSINT adapters, in place of the runtime's default.
/// <para>
/// It replaces rather than supplements the built-in logger because the built-in one writes the
/// request path verbatim, and one of these providers carries its credential there. Two rules apply
/// to every line: query values are dropped wholesale, and any configured credential is masked
/// wherever it appears. The first preserves the runtime's own default behaviour now that its logger
/// has been removed; the second is what that default never did.
/// </para>
/// <para>
/// What survives is the part worth having in a log — method, host, and path shape — so a failing poll
/// is still diagnosable from the logs alone.
/// </para>
/// </summary>
internal sealed partial class RedactingHttpClientLogger(
    ProviderSecretRedactor redactor,
    ILogger<RedactingHttpClientLogger> logger) : IHttpClientLogger
{
    public object? LogRequestStart(HttpRequestMessage request)
    {
        ArgumentNullException.ThrowIfNull(request);

        // Guarded because describing the request allocates, and at Debug level most deployments will
        // never read the result.
        if (logger.IsEnabled(LogLevel.Debug))
        {
            var target = Describe(request);
            LogStart(logger, request.Method, target);
        }

        // No state needs carrying to the completion callbacks: the framework supplies the elapsed
        // time, and the request message itself is handed back.
        return null;
    }

    public void LogRequestStop(
        object? context,
        HttpRequestMessage request,
        HttpResponseMessage response,
        TimeSpan elapsed)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(response);

        if (logger.IsEnabled(LogLevel.Debug))
        {
            var target = Describe(request);
            LogStop(logger, request.Method, target, (int)response.StatusCode, elapsed.TotalMilliseconds);
        }
    }

    public void LogRequestFailed(
        object? context,
        HttpRequestMessage request,
        HttpResponseMessage? response,
        Exception exception,
        TimeSpan elapsed)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(exception);

        if (!logger.IsEnabled(LogLevel.Warning))
        {
            return;
        }

        // The exception is rendered to text and redacted rather than handed to the logger as an
        // exception. A failure carries the request URL in its message often enough — a socket error, a
        // URI format error, or a provider echoing the request back — that logging the object directly
        // would reopen the hole this class exists to close.
        var failedTarget = Describe(request);
        var detail = redactor.Redact(exception.ToString());
        LogFailed(logger, request.Method, failedTarget, elapsed.TotalMilliseconds, detail);
    }

    /// <summary>
    /// The request target reduced to what is safe and useful: scheme, host, path with credentials
    /// masked, and a marker where a query string was rather than its values.
    /// </summary>
    private string Describe(HttpRequestMessage request)
    {
        if (request.RequestUri is not { } uri)
        {
            return "(no uri)";
        }

        // Absolute form, so a relative request made against a configured base address still names the
        // host it actually reached.
        var absolute = uri.IsAbsoluteUri ? uri : new Uri(new Uri("http://relative.invalid"), uri);
        var target = $"{absolute.Scheme}://{absolute.Authority}{absolute.AbsolutePath}";

        if (!string.IsNullOrEmpty(absolute.Query))
        {
            target += "?*";
        }

        return redactor.Redact(target);
    }

    [LoggerMessage(Level = LogLevel.Debug, Message = "Provider request starting: {Method} {Target}")]
    private static partial void LogStart(ILogger logger, HttpMethod method, string target);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Provider request finished: {Method} {Target} responded {StatusCode} in {ElapsedMilliseconds}ms")]
    private static partial void LogStop(ILogger logger, HttpMethod method, string target, int statusCode, double elapsedMilliseconds);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Provider request failed: {Method} {Target} after {ElapsedMilliseconds}ms. {Detail}")]
    private static partial void LogFailed(ILogger logger, HttpMethod method, string target, double elapsedMilliseconds, string detail);
}
