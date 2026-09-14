using Geopolitics.Domain;

namespace Geopolitics.Application.Abstractions;

/// <summary>
/// One report that said something about who holds a place, flattened for assessment.
/// </summary>
/// <param name="ObservationId">
/// The record itself. Carried through to the published assessment so a reader can reach the evidence
/// rather than being told how much of it there was — which is the difference between an assessment
/// and a guess wearing one's clothes.
/// </param>
/// <param name="SourceName">Who reported it, so independence can be counted.</param>
/// <param name="OccurredAt">When the source says it happened.</param>
/// <param name="Signal">What kind of control evidence this is.</param>
/// <param name="Basis">Where the signal came from, which decides the weight it carries.</param>
/// <param name="Actor">The actor it is about, in the source's own wording.</param>
/// <param name="PlaceName">The place the deterministic resolver accepted, not the one the source claimed.</param>
/// <param name="CountryCode">Where that place turned out to be.</param>
/// <param name="Latitude">Drawn only at the precision below.</param>
/// <param name="Longitude">Drawn only at the precision below.</param>
/// <param name="Precision">How precisely the place is known, carried so the map cannot overstate it.</param>
public sealed record ControlObservation(
    Guid ObservationId,
    string SourceName,
    DateTimeOffset OccurredAt,
    ControlSignal Signal,
    ControlEvidenceBasis Basis,
    string Actor,
    string PlaceName,
    string? CountryCode,
    double? Latitude,
    double? Longitude,
    LocationPrecision Precision);

/// <summary>
/// Reads the reports that carry control evidence, which is a small minority of the table.
/// </summary>
public interface IControlRepository
{
    /// <summary>
    /// Every placed report carrying a control signal since <paramref name="since"/>, newest first.
    /// </summary>
    /// <remarks>
    /// Placed only, and that is a real exclusion rather than a convenience. A control signal with no
    /// coordinate names a place this system could not resolve, so there is nowhere to assess and no
    /// honest way to attribute it — the same reason the coverage panel refuses to split its unplaced
    /// count by theatre.
    /// </remarks>
    Task<IReadOnlyList<ControlObservation>> ListSignalsAsync(
        DateTimeOffset since,
        CancellationToken cancellationToken);
}
