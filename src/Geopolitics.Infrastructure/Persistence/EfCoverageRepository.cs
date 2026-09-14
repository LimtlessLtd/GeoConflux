using Geopolitics.Application.Abstractions;
using Geopolitics.Application.Coverage;
using Geopolitics.Domain;
using Microsoft.EntityFrameworkCore;

namespace Geopolitics.Infrastructure.Persistence;

/// <summary>
/// Coverage counts against the relational store.
/// <para>
/// Grouped in the database rather than tallied in memory, for the same reason the analytics queries
/// are: the cost should grow with the number of precision levels and sources, which is small and
/// fixed, rather than with how much has been ingested.
/// </para>
/// </summary>
public sealed class EfCoverageRepository(GeopoliticsDbContext dbContext) : ICoverageRepository
{
    public async Task<TheatreTotals> CountPlacedAsync(Theatre theatre, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(theatre);

        var placed = InTheatre(theatre);

        var byPrecision = await placed
            .GroupBy(observation => observation.Location!.Precision)
            .Select(group => new CategoryCount(group.Key.ToString(), group.Count()))
            .ToListAsync(cancellationToken);

        // Ordered on the group's own count rather than on the projected record's property. The
        // latter reads more naturally and does not translate: the provider has no way to map a
        // record's property back to the aggregate it came from, so it gives up on the whole query.
        var bySource = await placed
            .GroupBy(observation => observation.SourceName)
            .OrderByDescending(group => group.Count())
            .Select(group => new CategoryCount(group.Key, group.Count()))
            .ToListAsync(cancellationToken);

        return new TheatreTotals(byPrecision.Sum(count => count.Count), byPrecision, bySource);
    }

    public Task<int> CountUnplacedAsync(CancellationToken cancellationToken) =>
        dbContext.Observations
            .AsNoTracking()

            // Named somewhere and still unplaced. An observation that named nowhere at all is not a
            // coverage failure — there was nothing to look up — and counting it here would inflate
            // the figure with items no gazetteer could ever have helped.
            .CountAsync(
                observation => observation.Location == null && observation.LocationName != null,
                cancellationToken);

    public async Task<BreadthTotals> CountBreadthAsync(CancellationToken cancellationToken)
    {
        var observations = dbContext.Observations.AsNoTracking();

        var byRegion = await observations
            .Where(observation => observation.Location != null && observation.Location.CountryCode != null)
            .GroupBy(observation => observation.Location!.CountryCode!)
            .OrderByDescending(group => group.Count())
            .Select(group => new CategoryCount(group.Key, group.Count()))
            .ToListAsync(cancellationToken);

        // Grouped on the tag itself rather than on a coalesced expression, then the unknowns are
        // counted separately and appended. Coalescing inside the GroupBy translates on some
        // providers and not others, and an unknown-language count that silently disappeared would
        // make the breakdown look more complete than the data is — which is the one thing a coverage
        // figure must never do.
        // A declared language beats a detected one, and the gap between them is not small. Detection
        // reads the script, which is all that can be established without a model: it separates Arabic
        // from Cyrillic and cannot separate French from Spanish from English, because all three are
        // written in the same alphabet. So a Latin-script feed was being counted as English whatever
        // it published in — which was near enough true when every feed was English and became a
        // misstatement the moment they were not.
        //
        // Counted as two queries and merged here rather than coalesced inside one GroupBy, for the
        // reason the paragraph above this method already gives: coalescing translates on some
        // providers and not others.
        var declared = await observations
            .Where(observation => observation.DeclaredLanguage != null)
            .GroupBy(observation => observation.DeclaredLanguage!)
            .Select(group => new CategoryCount(group.Key, group.Count()))
            .ToListAsync(cancellationToken);

        var detected = await observations
            .Where(observation => observation.DeclaredLanguage == null && observation.DetectedLanguage != null)
            .GroupBy(observation => observation.DetectedLanguage!)
            .Select(group => new CategoryCount(group.Key, group.Count()))
            .ToListAsync(cancellationToken);

        var byLanguage = declared
            .Concat(detected)
            .GroupBy(entry => entry.Category, StringComparer.OrdinalIgnoreCase)
            .Select(group => new CategoryCount(group.Key, group.Sum(entry => entry.Count)))
            .OrderByDescending(entry => entry.Count)
            .ThenBy(entry => entry.Category, StringComparer.Ordinal)
            .ToList();

        var unknownLanguage = await observations
            .CountAsync(
                observation => observation.DeclaredLanguage == null && observation.DetectedLanguage == null,
                cancellationToken);

        if (unknownLanguage > 0)
        {
            byLanguage.Add(new CategoryCount("unknown", unknownLanguage));
        }

        var byTier = await observations
            .GroupBy(observation => observation.Tier)
            .OrderByDescending(group => group.Count())
            .Select(group => new CategoryCount(group.Key.ToString(), group.Count()))
            .ToListAsync(cancellationToken);

        var byPlatform = await observations
            .Where(observation => observation.Platform != null)
            .GroupBy(observation => observation.Platform!)
            .OrderByDescending(group => group.Count())
            .Select(group => new CategoryCount(group.Key, group.Count()))
            .ToListAsync(cancellationToken);

        return new BreadthTotals(byRegion, byLanguage, byTier, byPlatform);
    }

    /// <summary>
    /// Placed observations inside one theatre.
    /// <para>
    /// Country code first, because it is indexed and selective, and then the latitude and longitude
    /// limits where a theatre is smaller than its country. Tigray is the only one of the three that
    /// needs the second half, and without it every report from anywhere in Ethiopia would be counted
    /// as Tigray coverage — which would make the one theatre whose thinness most needs stating look
    /// considerably better covered than it is.
    /// </para>
    /// </summary>
    private IQueryable<RawObservation> InTheatre(Theatre theatre)
    {
        var query = dbContext.Observations
            .AsNoTracking()
            .Where(observation =>
                observation.Location != null
                && observation.Location.CountryCode == theatre.CountryCode);

        if (theatre.Bounds is not { } bounds)
        {
            return query;
        }

        return query.Where(observation =>
            observation.Location!.Latitude >= bounds.South
            && observation.Location.Latitude <= bounds.North
            && observation.Location.Longitude >= bounds.West
            && observation.Location.Longitude <= bounds.East);
    }
}
