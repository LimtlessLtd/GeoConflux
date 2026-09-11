using Geopolitics.Application.Abstractions;
using Geopolitics.Application.Analytics;

namespace Geopolitics.Api.Endpoints;

/// <summary>
/// Analytical views over stored incidents and observations.
/// </summary>
public static class AnalyticsEndpoints
{
    public static IEndpointRouteBuilder MapAnalyticsEndpoints(this IEndpointRouteBuilder builder)
    {
        var analytics = builder.MapGroup("/api/analytics").WithTags("Analytics");

        analytics.MapGet(
            "/",
            async (string? window, IAnalyticsService service, CancellationToken cancellationToken) =>
            {
                // An unrecognised token is rejected rather than quietly defaulting. Silently serving
                // 24 hours to someone who asked for "1y" would answer a question they did not ask,
                // and every figure in the response would look like an answer to theirs.
                if (window is not null && !AnalyticsWindow.TryParse(window, out _))
                {
                    return Results.BadRequest(new
                    {
                        error = "Unsupported window.",
                        supported = AnalyticsWindow.All.Select(value => value.Token),
                    });
                }

                var report = await service.BuildAsync(AnalyticsWindow.Parse(window), cancellationToken);
                return Results.Ok(report);
            })
            .WithName("GetAnalytics")
            .WithSummary("Incident and observation analytics for one time window.");

        analytics.MapGet(
            "/windows",
            () => Results.Ok(AnalyticsWindow.All.Select(value => new
            {
                token = value.Token,
                label = value.Label,
                hours = value.Duration.TotalHours,
                bucketMinutes = value.BucketSize.TotalMinutes,
                buckets = value.BucketCount,
            })))
            .WithName("ListAnalyticsWindows")
            .WithSummary("The time windows analytics can be requested over.");

        return builder;
    }
}
