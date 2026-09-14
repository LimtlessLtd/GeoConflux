using Geopolitics.Application.Abstractions;
using Geopolitics.Application.Conflicts;
using Geopolitics.Domain;
using Microsoft.EntityFrameworkCore;

namespace Geopolitics.Infrastructure.Persistence;

/// <summary>
/// The reports behind per-conflict tempo, read from the relational store.
/// <para>
/// Unlike every other read model here this one materialises rows rather than pushing a
/// <c>GROUP BY</c> into the database, and the reason is structural rather than a shortcut. A report
/// belongs to a <em>list</em> of conflicts, stored as JSON, and SQLite cannot group by the elements
/// of that list. The projection is therefore cut to the four fields the tally needs, the window is
/// filtered in SQL, and the result is capped with the truncation reported rather than hidden.
/// </para>
/// </summary>
public sealed class EfConflictActivityRepository(GeopoliticsDbContext dbContext) : IConflictActivityRepository
{
    public async Task<ConflictActivitySample> SampleAsync(
        DateTimeOffset windowStart,
        DateTimeOffset windowEnd,
        int take,
        CancellationToken cancellationToken)
    {
        if (take <= 0)
        {
            return new ConflictActivitySample([], false);
        }

        // Duplicates are excluded and nothing else is. A held claim and a failed enrichment are both
        // still reports that reached this system about this conflict, which is exactly what tempo
        // measures; a redelivery of a report already counted is not.
        var query = dbContext.Observations
            .AsNoTracking()
            .Where(observation => observation.Status != ObservationStatus.Duplicate)

            // Event time where the source stated one, arrival time otherwise. The same convention the
            // rest of analytics uses, and the honest one for tempo: a coded record that arrives three
            // months late describes the week it happened in, not the week it was read in.
            .Where(observation =>
                (observation.OccurredAt ?? observation.ReceivedAt) >= windowStart
                && (observation.OccurredAt ?? observation.ReceivedAt) < windowEnd)
            .OrderByDescending(observation => observation.OccurredAt ?? observation.ReceivedAt)
            .Select(observation => new
            {
                observation.ConflictKeys,
                observation.SourceName,
                observation.IncidentId,
                Candidates = observation.ConflictCandidateKeys,
            });

        // One row over the cap, so the truncation is observed rather than inferred from a count that
        // happens to equal the limit.
        var rows = await query.Take(take + 1).ToListAsync(cancellationToken);
        var truncated = rows.Count > take;

        return new ConflictActivitySample(
            [.. rows
                .Take(take)
                .Select(row => new ConflictObservationSample(
                    row.ConflictKeys,
                    row.SourceName,
                    row.IncidentId,
                    row.Candidates.Count))],
            truncated);
    }

    public async Task<IReadOnlyList<ConflictEvidence>> EvidenceAsync(
        string conflictKey,
        DateTimeOffset windowStart,
        DateTimeOffset windowEnd,
        int take,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(conflictKey) || take <= 0)
        {
            return [];
        }

        // The membership filter is applied after materialisation for the same reason the tally is:
        // the keys are a JSON list and SQLite cannot search inside it. The window and the status are
        // applied in SQL, which is what keeps the materialised set small.
        var rows = await dbContext.Observations
            .AsNoTracking()
            .Where(observation => observation.Status != ObservationStatus.Duplicate)
            .Where(observation =>
                (observation.OccurredAt ?? observation.ReceivedAt) >= windowStart
                && (observation.OccurredAt ?? observation.ReceivedAt) < windowEnd)
            .OrderByDescending(observation => observation.OccurredAt ?? observation.ReceivedAt)
            .Select(observation => new
            {
                observation.ConflictKeys,
                observation.Title,
                observation.Summary,
                observation.SourceName,
                PlaceName = observation.Location == null ? observation.LocationName : observation.Location.Name,
                When = observation.OccurredAt ?? observation.ReceivedAt,
            })
            .Take(MaxEvidenceScan)
            .ToListAsync(cancellationToken);

        return
        [
            .. rows
                .Where(row => row.ConflictKeys.Contains(conflictKey, StringComparer.Ordinal))
                .Take(take)
                .Select(row => new ConflictEvidence(
                    row.Title,
                    row.Summary,
                    row.SourceName,
                    row.PlaceName,
                    row.When)),
        ];
    }

    /// <summary>
    /// How many of a window's reports are read before the membership filter is applied. A bound on
    /// the cost of a filter the database cannot do, and generous enough that a conflict with any real
    /// coverage fills its evidence quota long before this is reached.
    /// </summary>
    private const int MaxEvidenceScan = 5000;
}
