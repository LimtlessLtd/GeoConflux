using Geopolitics.Application.Contracts;

namespace Geopolitics.Application.Abstractions;

public enum ProcessingOutcome
{
    /// <summary>The observation was stored and associated with an incident.</summary>
    Persisted = 0,

    /// <summary>An identical report had already been accepted; the delivery was recorded and stopped.</summary>
    Duplicate,

    /// <summary>Processing failed. The observation is retained with a failure reason where possible.</summary>
    Failed,
}

/// <param name="Outcome">What the pipeline decided.</param>
/// <param name="ObservationId">Identifier of the stored observation, when one was created.</param>
/// <param name="IncidentId">Incident the observation was associated with, when correlation succeeded.</param>
/// <param name="IncidentCreated">True when this observation opened a new incident.</param>
/// <param name="FailureReason">Why processing failed, when it did.</param>
public sealed record ObservationProcessingResult(
    ProcessingOutcome Outcome,
    Guid? ObservationId,
    Guid? IncidentId,
    bool IncidentCreated,
    string? FailureReason)
{
    public static ObservationProcessingResult Failed(Guid? observationId, string reason) =>
        new(ProcessingOutcome.Failed, observationId, null, false, reason);
}

/// <summary>
/// Runs one envelope through normalisation, deduplication, location resolution, correlation,
/// persistence, and realtime publication. Implementations must not throw for expected failures;
/// they record the failure against the observation and report it in the result.
/// </summary>
public interface IObservationProcessor
{
    Task<ObservationProcessingResult> ProcessAsync(ObservationEnvelope envelope, CancellationToken cancellationToken);
}

/// <summary>
/// Raised by a repository when a concurrent writer already stored an observation with the same
/// fingerprint. The uniqueness check and the insert cannot be atomic across processor threads, so
/// the database constraint is the authority and the processor treats this as a duplicate.
/// </summary>
public sealed class DuplicateObservationException(string fingerprint, Exception? innerException = null)
    : Exception($"An observation with fingerprint '{fingerprint}' already exists.", innerException)
{
    public string Fingerprint { get; } = fingerprint;
}
