using Geopolitics.Application.Abstractions;
using Geopolitics.Domain;

namespace Geopolitics.Api.Endpoints;

/// <summary>
/// The trained severity model, exposed directly so it can be exercised and compared without going
/// through ingestion.
/// </summary>
public static class SeverityEndpoints
{
    public static IEndpointRouteBuilder MapSeverityEndpoints(this IEndpointRouteBuilder builder)
    {
        var severity = builder.MapGroup("/api/severity").WithTags("Severity model");

        severity.MapGet(
            "/model",
            (ISeverityModel model) => Results.Ok(new
            {
                ready = model.IsReady,
                version = model.Version,
                method = model.IsReady ? "ml:sdca-maximum-entropy" : null,
                notice = Notice,
            }))
            .WithName("DescribeSeverityModel")
            .WithSummary("Whether the severity model is available, and which model it is.");

        severity.MapPost(
            "/predict",
            async (SeverityRequest? request, ISeverityModel model, CancellationToken cancellationToken) =>
            {
                if (request is null || string.IsNullOrWhiteSpace(request.Content))
                {
                    return Results.BadRequest(new { error = "A report body is required." });
                }

                if (!model.IsReady)
                {
                    // 503 rather than a default severity. A disabled or unfitted model has no opinion,
                    // and inventing one here would be indistinguishable from a real prediction.
                    return Results.Problem(
                        detail: "The severity model is not available in this deployment.",
                        statusCode: StatusCodes.Status503ServiceUnavailable);
                }

                var prediction = await model.PredictAsync(
                    SeverityFeatures.From(
                        request.Title,
                        request.Content,
                        request.EventType ?? EventType.Other,

                        // Clamped rather than rejected: these describe corroboration the caller may
                        // legitimately not have, and a sensible default is more useful than a 400.
                        Math.Clamp(request.SourceCount ?? 1, 1, 100),
                        Math.Clamp(request.SourceConfidence ?? 0.5, 0, 1),
                        Math.Clamp(request.EntityCount ?? 0, 0, 100),
                        request.HasLocation ?? false),
                    cancellationToken);

                return prediction is null
                    ? Results.Problem(
                        detail: "The severity model returned no prediction.",
                        statusCode: StatusCodes.Status503ServiceUnavailable)
                    : Results.Ok(new
                    {
                        prediction.Severity,
                        prediction.Confidence,
                        prediction.Scores,
                        prediction.ModelVersion,
                        prediction.Method,

                        // Carried in the payload, not only in the docs. This endpoint returns a
                        // confident-looking label from soft inputs, and the caveat has to travel
                        // with it.
                        notice = Notice,
                    });
            })
            .WithName("PredictSeverity")
            .WithSummary("Scores a report with the trained severity model.");

        return builder;
    }

    private const string Notice =
        "A conventional model trained on a small synthetic, author-labelled corpus. It is a second "
        + "opinion for comparison against the enrichment path and is never allowed to set the severity "
        + "an incident is stored with. See tests/data/severity-model/RESULTS.md for its measured "
        + "performance and limitations.";
}

/// <summary>
/// Prediction request. Mirrors <see cref="SeverityFeatures"/> because the model must be asked in the
/// same terms it was trained in — a caller who could supply a feature the trainer never saw would be
/// asking a different model.
/// </summary>
public sealed record SeverityRequest
{
    public string? Title { get; init; }

    public string? Content { get; init; }

    public EventType? EventType { get; init; }

    public int? SourceCount { get; init; }

    public double? SourceConfidence { get; init; }

    public int? EntityCount { get; init; }

    public bool? HasLocation { get; init; }
}
