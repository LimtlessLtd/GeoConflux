using Geopolitics.Application.Abstractions;
using Geopolitics.Domain;
using Microsoft.EntityFrameworkCore;

namespace Geopolitics.Infrastructure.Persistence;

/// <summary>
/// Reads the reports that carry control evidence, which is a small minority of the table.
/// <para>
/// The filtered index on signal and time serves this directly. Signal leads it because it is
/// overwhelmingly the selective half: almost every observation says nothing about control, including
/// almost every report of fighting.
/// </para>
/// </summary>
public sealed class EfControlRepository(GeopoliticsDbContext dbContext) : IControlRepository
{
    /// <summary>
    /// A ceiling on one assessment's read. Generous against any real volume of control evidence,
    /// and present because this is an unauthenticated endpoint: an assessment is a whole-table
    /// question, and the one thing it must not become is a way to ask for the whole table.
    /// </summary>
    private const int MaxSignals = 5_000;

    public async Task<IReadOnlyList<ControlObservation>> ListSignalsAsync(
        DateTimeOffset since,
        CancellationToken cancellationToken)
    {
        var rows = await dbContext.Observations
            .AsNoTracking()
            .Where(observation => observation.ControlSignal != ControlSignal.None)
            .Where(observation => observation.ControlActor != null)
            .Where(observation => observation.ControlBasis != null)

            // Placed only. A control signal with no coordinate names a place this system could not
            // resolve, so there is nowhere to assess and no honest way to attribute it.
            .Where(observation => observation.Location != null && observation.Location.Name != null)
            .Where(observation => observation.OccurredAt != null && observation.OccurredAt >= since)
            .OrderByDescending(observation => observation.OccurredAt)
            .Take(MaxSignals)
            .Select(observation => new
            {
                observation.Id,
                observation.SourceName,
                OccurredAt = observation.OccurredAt!.Value,
                observation.ControlSignal,
                Basis = observation.ControlBasis!.Value,
                Actor = observation.ControlActor!,
                PlaceName = observation.Location!.Name!,
                observation.Location.CountryCode,
                observation.Location.Latitude,
                observation.Location.Longitude,
                observation.Location.Precision,
            })
            .ToListAsync(cancellationToken);

        return
        [
            .. rows.Select(row => new ControlObservation(
                row.Id,
                row.SourceName,
                row.OccurredAt,
                row.ControlSignal,
                row.Basis,
                row.Actor,
                row.PlaceName,
                row.CountryCode,
                row.Latitude,
                row.Longitude,
                row.Precision)),
        ];
    }
}
