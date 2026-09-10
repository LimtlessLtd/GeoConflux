using Geopolitics.Application;
using Geopolitics.Application.Abstractions;
using Geopolitics.Application.Contracts;
using Geopolitics.Domain;

namespace Geopolitics.Api.Endpoints;

/// <summary>
/// Manual observation submission and the read model behind the live feed.
/// </summary>
public static class ObservationEndpoints
{
    public static IEndpointRouteBuilder MapObservationEndpoints(this IEndpointRouteBuilder builder)
    {
        var observations = builder.MapGroup("/api/observations").WithTags("Observations");

        observations.MapGet(
            "/",
            async (int? take, IObservationQueryService service, CancellationToken cancellationToken) =>
                Results.Ok(await service.ListRecentAsync(take ?? 50, cancellationToken)))
            .WithName("ListRecentObservations")
            .WithSummary("Lists the most recently received observations, newest first.");

        observations.MapGet(
            "/by-incident/{incidentId:guid}",
            async (Guid incidentId, IObservationQueryService service, CancellationToken cancellationToken) =>
                Results.Ok(await service.ListByIncidentAsync(incidentId, cancellationToken)))
            .WithName("ListObservationsForIncident")
            .WithSummary("Lists the evidence correlated with one incident.");

        observations.MapPost(
            "/",
            async (
                ObservationSubmission submission,
                IObservationIngestionService ingestion,
                CancellationToken cancellationToken) =>
            {
                if (submission is null)
                {
                    return Results.BadRequest(new { error = "A submission body is required." });
                }

                var result = await ingestion.IngestAsync(submission.ToEnvelope(), cancellationToken);

                if (!result.Accepted)
                {
                    return Results.ValidationProblem(new Dictionary<string, string[]>
                    {
                        ["submission"] = [result.RejectionReason ?? "The submission was rejected."],
                    });
                }

                // 202, not 201: the observation has been queued, not yet processed, and it may still
                // turn out to be a duplicate or fail enrichment. Claiming a created incident here
                // would be reporting an outcome that has not happened.
                return Results.Accepted(
                    "/api/observations",
                    new { status = "queued", detail = "The observation was queued for processing." });
            })
            .WithName("SubmitObservation")
            .WithSummary("Queues a manually submitted observation for processing.");

        return builder;
    }
}

/// <summary>
/// Public submission contract. Deliberately narrower than <see cref="ObservationEnvelope"/>: a caller
/// may not mark its own submission as demo data, nor set a trace identifier, because both would let
/// external input misrepresent the provenance of what it sends.
/// </summary>
public sealed record ObservationSubmission
{
    public string? SourceName { get; init; }

    public string? Title { get; init; }

    public string? Content { get; init; }

    public string? LocationName { get; init; }

    public double? Latitude { get; init; }

    public double? Longitude { get; init; }

    public string? CountryCode { get; init; }

    public EventType? EventType { get; init; }

    public Severity? Severity { get; init; }

    public DateTimeOffset? OccurredAt { get; init; }

    public ObservationEnvelope ToEnvelope() => new()
    {
        // Manual submissions are namespaced so they are always distinguishable from adapter output.
        SourceName = string.IsNullOrWhiteSpace(SourceName) ? "manual" : $"manual:{SourceName.Trim()}",
        Kind = ObservationKind.Manual,
        Content = Content ?? string.Empty,
        Title = Title,
        OccurredAt = OccurredAt,
        DeclaredLocationName = LocationName,
        DeclaredLatitude = Latitude,
        DeclaredLongitude = Longitude,
        DeclaredCountryCode = CountryCode,
        DeclaredEventType = EventType,
        DeclaredSeverity = Severity,
        IsDemo = false,
    };
}
