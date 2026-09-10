using Geopolitics.Domain;

namespace Geopolitics.Application.Abstractions;

/// <param name="Incident">Existing incident this observation belongs to, or <see langword="null"/> to open a new one.</param>
/// <param name="Confidence">0-1 score for the match.</param>
/// <param name="Rationale">Human-readable explanation, recorded so correlation decisions are reviewable.</param>
public sealed record CorrelationAssessment(GeopoliticalIncident? Incident, double Confidence, string Rationale)
{
    public bool IsCorrelated => Incident is not null;

    public static CorrelationAssessment NewIncident(string rationale) => new(null, 0, rationale);
}

/// <summary>
/// Decides whether an observation describes an event already tracked as an incident. Per ADR 006,
/// duplicate evidence is linked rather than discarded so provenance survives.
/// </summary>
public interface IIncidentCorrelator
{
    Task<CorrelationAssessment> CorrelateAsync(RawObservation observation, CancellationToken cancellationToken);
}
