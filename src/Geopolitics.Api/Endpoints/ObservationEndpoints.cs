using Geopolitics.Application;
using Geopolitics.Application.Abstractions;
using Geopolitics.Application.Contracts;
using Geopolitics.Domain;
using Microsoft.AspNetCore.RateLimiting;

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

                // The queue makes producers wait when it is full. An adapter should wait; an HTTP
                // caller should be answered. Bounding the wait here keeps a saturated pipeline from
                // being expressible as an unbounded number of held-open connections.
                using var queueWait = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                queueWait.CancelAfter(SubmissionProtection.QueueWait);

                IngestionResult result;

                try
                {
                    result = await ingestion.IngestAsync(submission.ToEnvelope(), queueWait.Token);
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    // The caller is still there; it is this process that could not take the work.
                    // 503 with Retry-After says that honestly, where a 500 would blame the request.
                    return Results.Problem(
                        title: "The pipeline is saturated.",
                        detail: "The processing queue is full. The submission was not accepted; retry shortly.",
                        statusCode: StatusCodes.Status503ServiceUnavailable);
                }

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
            .WithSummary("Queues a manually submitted observation for processing.")
            .RequireRateLimiting(SubmissionProtection.PolicyName);

        return builder;
    }
}

/// <summary>
/// Public submission contract. Deliberately narrower than <see cref="ObservationEnvelope"/>: a caller
/// may not mark its own provenance, nor set a trace identifier, because both would let external
/// input misrepresent where what it sends came from.
/// <para>
/// It carries no latitude or longitude either, and that omission is the point. The endpoint used to
/// accept a coordinate pair from any caller on the network, and the resolver honoured it as
/// <c>SourceProvided</c> — an exact position at 0.95 confidence, from an anonymous stranger. That is
/// the failure ADR 005 exists to prevent, and it was reachable in the default configuration. A
/// submission now names a place and the gazetteer places it, or it stays unplaced.
/// </para>
/// </summary>
public sealed record ObservationSubmission
{
    public string? SourceName { get; init; }

    public string? Title { get; init; }

    public string? Content { get; init; }

    public string? LocationName { get; init; }

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
        DeclaredCountryCode = CountryCode,
        DeclaredEventType = EventType,
        DeclaredSeverity = Severity,
        Provenance = ObservationProvenance.Collected,
    };
}
