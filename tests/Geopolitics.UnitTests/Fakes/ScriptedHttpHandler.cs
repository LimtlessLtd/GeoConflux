using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;

namespace Geopolitics.UnitTests.Fakes;

/// <summary>
/// Serves a fixed sequence of HTTP responses and then stops the source that is calling it.
/// <para>
/// A polling adapter runs until it is cancelled, so a test needs some way to say "that is enough".
/// Exhausting a script is that signal, and it is a better one than a timer: the test states exactly
/// which responses the provider will give and in what order, so retry behaviour becomes something the
/// test asserts on rather than something it tolerates. When the script runs out the handler cancels
/// the token the source is enumerating under, which ends the enumeration at its next poll.
/// </para>
/// </summary>
internal sealed class ScriptedHttpHandler(
    CancellationTokenSource stopSignal,
    params Func<HttpRequestMessage, HttpResponseMessage>[] script) : HttpMessageHandler
{
    private int served = -1;

    /// <summary>
    /// Every request reaching the transport, including retried ones, in the fully escaped form that
    /// would go on the wire. <see cref="Uri.ToString"/> is deliberately not used: it returns the
    /// unescaped canonical form, which would hide exactly the escaping bugs a test wants to catch.
    /// </summary>
    public ConcurrentQueue<string> Requests { get; } = new();

    public int RequestCount => Requests.Count;

    /// <summary>
    /// How many scripted responses were actually delivered, which is the count a test about retry
    /// behaviour means. It excludes the request that runs past the end of the script, because that
    /// one exists only to end the run.
    /// </summary>
    public int ScriptedResponsesServed => Math.Min(Requests.Count, script.Length);

    /// <summary>A response with a body and a status, which is all these adapters read.</summary>
    public static Func<HttpRequestMessage, HttpResponseMessage> Respond(
        string body,
        HttpStatusCode status = HttpStatusCode.OK,
        string mediaType = "application/xml") =>
        _ => new HttpResponseMessage(status) { Content = new StringContent(body, System.Text.Encoding.UTF8, mediaType) };

    /// <summary>
    /// A rate-limit response carrying the delay the provider wants observed. <c>Retry-After: 0</c>
    /// keeps the test fast while still exercising the header path rather than the backoff curve.
    /// </summary>
    public static Func<HttpRequestMessage, HttpResponseMessage> RateLimited(int retryAfterSeconds = 0) =>
        _ =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests)
            {
                Content = new StringContent("rate limited"),
            };

            response.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(retryAfterSeconds));
            return response;
        };

    public static Func<HttpRequestMessage, HttpResponseMessage> Status(HttpStatusCode status) =>
        _ => new HttpResponseMessage(status) { Content = new StringContent(status.ToString()) };

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Requests.Enqueue(request.RequestUri!.AbsoluteUri);
        var index = Interlocked.Increment(ref served);

        if (index < script.Length)
        {
            return Task.FromResult(script[index](request));
        }

        // Cancelled outside any lock, and only once the script is spent, so the responses the test
        // did ask for are all delivered and fully read before anything is interrupted.
        stopSignal.Cancel();
        throw new OperationCanceledException(stopSignal.Token);
    }
}
