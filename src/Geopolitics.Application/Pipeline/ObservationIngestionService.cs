using Geopolitics.Application.Abstractions;
using Geopolitics.Application.Contracts;
using Geopolitics.Domain;
using Microsoft.Extensions.Logging;

namespace Geopolitics.Application.Pipeline;

/// <summary>
/// The one gate every observation passes through before reaching the queue, whether it comes from a
/// polling adapter, a collection bundle, or the public submission endpoint. Validation lives here so that
/// no ingestion path can enqueue an envelope the processor cannot handle.
/// </summary>
public sealed partial class ObservationIngestionService(
    IObservationQueueWriter queue,
    PipelineDiagnostics diagnostics,
    ILogger<ObservationIngestionService> logger) : IObservationIngestionService
{
    private const int MaxContentLength = 20_000;

    public async ValueTask<IngestionResult> IngestAsync(ObservationEnvelope envelope, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(envelope);

        if (Validate(envelope) is { } rejection)
        {
            diagnostics.ItemsFailed.Add(1, new KeyValuePair<string, object?>("source", envelope.SourceName ?? "unknown"));
            LogRejected(logger, envelope.SourceName ?? "unknown", rejection);
            return IngestionResult.Rejected(rejection);
        }

        await queue.EnqueueAsync(envelope, cancellationToken);
        diagnostics.ItemsReceived.Add(1, new KeyValuePair<string, object?>("source", envelope.SourceName));
        return IngestionResult.Queued();
    }

    /// <summary>
    /// Returns a rejection reason, or <see langword="null"/> when the envelope is acceptable. External
    /// payloads are untrusted, so bounds are enforced here rather than at the persistence layer.
    /// </summary>
    private static string? Validate(ObservationEnvelope envelope)
    {
        if (string.IsNullOrWhiteSpace(envelope.SourceName))
        {
            return "A source name is required.";
        }

        if (string.IsNullOrWhiteSpace(envelope.Content))
        {
            return "Observation content is required.";
        }

        if (envelope.Content.Length > MaxContentLength)
        {
            return $"Observation content exceeds the {MaxContentLength} character limit.";
        }

        if (envelope.DeclaredLatitude is { } latitude && latitude is < -90 or > 90)
        {
            return "Declared latitude must be between -90 and 90 degrees.";
        }

        if (envelope.DeclaredLongitude is { } longitude && longitude is < -180 or > 180)
        {
            return "Declared longitude must be between -180 and 180 degrees.";
        }

        // A half-supplied coordinate pair is a provider bug; accepting it would silently place the
        // report on a meridian or the equator.
        if (envelope.DeclaredLatitude is null != (envelope.DeclaredLongitude is null))
        {
            return "Declared coordinates must supply both latitude and longitude.";
        }

        // Refused rather than quietly dropped. Anything reaching here with a coordinate it is not
        // entitled to is either a bug in an adapter or a caller trying to place a pin, and both are
        // worth a stated rejection: silently ignoring the field would let the sender keep believing
        // it had worked.
        if (envelope.DeclaredLatitude is not null && !envelope.Kind.MayDeclareCoordinates())
        {
            return $"A {envelope.Kind} observation may not declare coordinates; "
                + "name a place instead and the resolver will place it.";
        }

        // A precision describes a coordinate, so without one it describes nothing. Accepting it
        // would let a caller attach a confident-sounding qualifier to a position the resolver is
        // about to derive from a place name, which is a claim about someone else's work.
        if (envelope.DeclaredPrecision is not null && envelope.DeclaredLatitude is null)
        {
            return "A declared precision describes declared coordinates, so it may not be supplied without them.";
        }

        return null;
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Rejected an observation from {SourceName}: {Reason}")]
    private static partial void LogRejected(ILogger logger, string sourceName, string reason);
}
