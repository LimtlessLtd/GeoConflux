using Geopolitics.Application.Abstractions;
using Geopolitics.Application.Conflicts;
using Geopolitics.Application.Coverage;
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

        analytics.MapGet(
            "/conflicts",
            async (string? window, IConflictActivityService service, CancellationToken cancellationToken) =>
            {
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
            .WithName("GetConflictActivity")
            .WithSummary("What reached this system about each conflict, against how many sources said it.")

            // Named for what it reports rather than for what it counts. A count of reports is not a
            // count of events, and the response says so in a field rather than leaving it to a
            // reader who may only look at the numbers.
            .WithDescription(
                "Per-conflict reporting volume for one window, each figure carried beside the number "
                + "of sources that produced it, and each conflict compared only against its own "
                + "previous window.");

        analytics.MapGet(
            "/conflicts/{key}/narrative",
            async (
                string key,
                string? window,
                IConflictNarrativeService service,
                CancellationToken cancellationToken) =>
            {
                if (window is not null && !AnalyticsWindow.TryParse(window, out _))
                {
                    return Results.BadRequest(new
                    {
                        error = "Unsupported window.",
                        supported = AnalyticsWindow.All.Select(value => value.Token),
                    });
                }

                var narrative = await service.BuildAsync(key, AnalyticsWindow.Parse(window), cancellationToken);

                // A conflict the register does not hold is a 404. A conflict with too little evidence
                // to characterise is a 200 carrying that sentence, because "too little to say" is an
                // answer and an empty panel is not.
                return narrative is null ? Results.NotFound() : Results.Ok(narrative);
            })
            .WithName("GetConflictNarrative")
            .WithSummary("What one window of reports about one conflict says, or why that cannot be said.");

        analytics.MapGet(
            "/coverage",
            async (ICoverageService service, CancellationToken cancellationToken) =>
                Results.Ok(await service.BuildAsync(cancellationToken)))
            .WithName("GetCoverage")
            .WithSummary("What has been placed per theatre, how precisely, and what the gaps are.");

        return builder;
    }
}
