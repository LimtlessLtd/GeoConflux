using System.Globalization;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.RateLimiting;

namespace Geopolitics.Api;

/// <summary>
/// The controls that apply to the one endpoint anyone on the network can write to.
/// <para>
/// Everything else this application exposes is a read. <c>POST /api/observations</c> is the single
/// place where an unauthenticated caller can put work into the pipeline, so it is the place where
/// "no authentication is required for this project" has to be paid for with limits instead.
/// </para>
/// </summary>
public static class SubmissionProtection
{
    /// <summary>Named so the endpoint declares the policy it is under rather than inheriting one.</summary>
    public const string PolicyName = "observation-submission";

    /// <summary>
    /// How long a submission may wait for space in the bounded queue before the caller is told to
    /// come back later.
    /// <para>
    /// The queue blocks producers when it is full, which is the correct behaviour for a polling
    /// adapter: a feed that cannot be enqueued should slow down rather than be dropped. It is the
    /// wrong behaviour for an HTTP request, because a request that waits is a connection held open,
    /// and a caller can hold as many as they like. Bounding the wait turns backpressure into an
    /// answer instead of a hostage.
    /// </para>
    /// </summary>
    public static readonly TimeSpan QueueWait = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Submissions allowed per client per window. Set for a human or a small script filing reports,
    /// not for a feed: adapters have their own ingestion path and are not subject to this.
    /// </summary>
    private const int PermitsPerWindow = 30;

    private static readonly TimeSpan Window = TimeSpan.FromMinutes(1);

    public static IServiceCollection AddSubmissionRateLimiting(this IServiceCollection services)
    {
        services.AddRateLimiter(limiter =>
        {
            limiter.AddPolicy(PolicyName, context =>
            {
                ArgumentNullException.ThrowIfNull(context);

                // Partitioned by remote address, so one noisy client cannot spend everyone else's
                // budget. There is no proxy in front of this application in any supported
                // deployment, so the connection's address is the caller's address; behind a reverse
                // proxy this would need forwarded-header processing to stay meaningful, and would
                // otherwise collapse every caller into one partition.
                var client = context.Connection.RemoteIpAddress?.ToString() ?? "unknown";

                return RateLimitPartition.GetFixedWindowLimiter(client, _ => new FixedWindowRateLimiterOptions
                {
                    PermitLimit = PermitsPerWindow,
                    Window = Window,

                    // Rejected outright rather than queued. A caller over the limit should be told so
                    // immediately; holding their request to release it later is the same resource
                    // problem this exists to prevent.
                    QueueLimit = 0,
                });
            });

            limiter.OnRejected = async (context, cancellationToken) =>
            {
                context.HttpContext.Response.StatusCode = StatusCodes.Status429TooManyRequests;

                if (context.Lease.TryGetMetadata(MetadataName.RetryAfter, out var retryAfter))
                {
                    context.HttpContext.Response.Headers.RetryAfter =
                        ((int)retryAfter.TotalSeconds).ToString(CultureInfo.InvariantCulture);
                }

                await context.HttpContext.Response.WriteAsJsonAsync(
                    new { error = "Too many submissions from this client. Try again shortly." },
                    cancellationToken);
            };
        });

        return services;
    }
}
