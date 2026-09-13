using System.Globalization;

namespace Geopolitics.Infrastructure.Sources.Providers;

/// <param name="Kept">Detections that survived every filter, newest first.</param>
/// <param name="BelowConfidence">Rejected as likely sun glint or cloud edge.</param>
/// <param name="BelowPower">Rejected for burning too weakly to be what this system looks for.</param>
/// <param name="Daytime">Rejected for being daytime, when night-only selection is on.</param>
/// <param name="PersistentSource">
/// Rejected for burning at the same place on too many separate days to be an event. This is the
/// count that matters most in Yemen, where gas flares burn continuously.
/// </param>
public sealed record FirmsFilterResult(
    IReadOnlyList<FirmsHotspot> Kept,
    int BelowConfidence,
    int BelowPower,
    int Daytime,
    int PersistentSource);

/// <summary>
/// Separates the thermal detections that might mean something from the overwhelming majority that do
/// not.
/// <para>
/// Fire is not war. FIRMS reports heat, and in the theatres this system covers most of that heat is
/// agriculture and industry: gas flaring burns continuously across Yemen and reads as a permanent
/// detection, and seasonal agricultural burning produces thousands of detections a week across
/// exactly the regions of Ethiopia in question. Enabling this adapter for those countries without
/// filtering would fill the map with farming and label it conflict — which is why the filtering is
/// the work and the adapter never was.
/// </para>
/// <para>
/// A pure function over a batch, deliberately. Every rule here is a judgement about what a detection
/// probably is, and judgements of that kind should be assertable directly against a list of rows
/// rather than only observable through an HTTP poll.
/// </para>
/// </summary>
public static class FirmsConflictFilter
{
    /// <summary>
    /// How coarsely a detection's position is rounded when deciding whether two of them are the same
    /// source. A hundredth of a degree is roughly a kilometre, which comfortably covers the jitter
    /// between passes for a 375 m VIIRS pixel without merging genuinely separate fires.
    /// </summary>
    private const int PersistenceDecimals = 2;

    /// <summary>
    /// Applies every configured filter, and reports what each one rejected.
    /// <para>
    /// The counts are returned rather than logged in passing because they are the diagnosis. A poll
    /// that yields nothing has said something useful if it also says that four hundred detections
    /// were dropped as persistent sources; the same empty result with no breakdown is
    /// indistinguishable from a broken credential.
    /// </para>
    /// </summary>
    public static FirmsFilterResult Apply(IReadOnlyList<FirmsHotspot> hotspots, FirmsProviderOptions settings)
    {
        ArgumentNullException.ThrowIfNull(hotspots);
        ArgumentNullException.ThrowIfNull(settings);

        var persistent = PersistentSources(hotspots, settings.PersistentSourceDays);

        var kept = new List<FirmsHotspot>(hotspots.Count);
        var belowConfidence = 0;
        var belowPower = 0;
        var daytime = 0;
        var persistentSource = 0;

        foreach (var hotspot in hotspots.OrderByDescending(value => value.AcquiredAt))
        {
            if (hotspot.Confidence < settings.MinimumConfidence)
            {
                belowConfidence++;
                continue;
            }

            if (settings.NightOnly && !hotspot.IsNight)
            {
                daytime++;
                continue;
            }

            // A detection with no reported power is not treated as a zero. FIRMS omits the column on
            // some datasets, and reading "not stated" as "too weak" would silently empty the feed for
            // everyone using one of them.
            if (settings.MinimumRadiativePowerMegawatts > 0
                && hotspot.RadiativePowerMegawatts is { } power
                && power < settings.MinimumRadiativePowerMegawatts)
            {
                belowPower++;
                continue;
            }

            if (persistent.Contains(CellOf(hotspot)))
            {
                persistentSource++;
                continue;
            }

            kept.Add(hotspot);
        }

        return new FirmsFilterResult(kept, belowConfidence, belowPower, daytime, persistentSource);
    }

    /// <summary>
    /// Locations burning on enough separate days to be infrastructure rather than an event.
    /// <para>
    /// This is the flare mask, and it is computed from the data instead of from a published list of
    /// flare sites. That is a real trade. A published list would be authoritative and would work on
    /// the first poll; deriving it means the mask only sees as far back as the request window, so a
    /// deployment wanting it to work must ask for several days at a time. What deriving it buys is
    /// that it needs no external dataset, stays correct when a new flare is lit, and cannot go stale.
    /// </para>
    /// <para>
    /// Distinct days rather than distinct detections, because a satellite passing twice in one night
    /// over a single genuine fire would otherwise look exactly like a permanent source.
    /// </para>
    /// </summary>
    private static HashSet<string> PersistentSources(IReadOnlyList<FirmsHotspot> hotspots, int days)
    {
        if (days <= 1)
        {
            return [];
        }

        var byCell = new Dictionary<string, HashSet<DateOnly>>(StringComparer.Ordinal);

        foreach (var hotspot in hotspots)
        {
            var cell = CellOf(hotspot);

            if (!byCell.TryGetValue(cell, out var dates))
            {
                byCell[cell] = dates = [];
            }

            dates.Add(DateOnly.FromDateTime(hotspot.AcquiredAt.UtcDateTime));
        }

        return [.. byCell.Where(entry => entry.Value.Count >= days).Select(entry => entry.Key)];
    }

    private static string CellOf(FirmsHotspot hotspot) => string.Create(
        CultureInfo.InvariantCulture,
        $"{Math.Round(hotspot.Latitude, PersistenceDecimals):F2},{Math.Round(hotspot.Longitude, PersistenceDecimals):F2}");
}
