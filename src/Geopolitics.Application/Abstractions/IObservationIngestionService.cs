using Geopolitics.Application.Contracts;

namespace Geopolitics.Application.Abstractions;

/// <param name="Accepted">True when the envelope was queued for processing.</param>
/// <param name="RejectionReason">Why the envelope was refused, when it was.</param>
public sealed record IngestionResult(bool Accepted, string? RejectionReason)
{
    public static IngestionResult Queued() => new(true, null);

    public static IngestionResult Rejected(string reason) => new(false, reason);
}

/// <summary>
/// Validates and queues observations. Acceptance means the envelope is well-formed and queued, not
/// that it has been processed; callers should not present acceptance as confirmation of an incident.
/// </summary>
public interface IObservationIngestionService
{
    ValueTask<IngestionResult> IngestAsync(ObservationEnvelope envelope, CancellationToken cancellationToken);
}
