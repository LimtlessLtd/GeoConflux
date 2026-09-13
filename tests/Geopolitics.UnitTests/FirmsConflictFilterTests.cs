using Geopolitics.Infrastructure.Sources.Providers;

namespace Geopolitics.UnitTests;

/// <summary>
/// Telling the thermal detections that might mean something from the overwhelming majority that do
/// not.
/// <para>
/// Fire is not war. Gas flaring burns continuously across Yemen and reads as a permanent detection,
/// and seasonal agricultural burning produces thousands of detections a week across exactly the
/// regions of Ethiopia this system is asked to cover. Enabling the adapter for those countries
/// without this filtering would fill the map with farming and label it conflict, which is why the
/// filtering is the work and the adapter never was.
/// </para>
/// </summary>
public sealed class FirmsConflictFilterTests
{
    private static string Fixture(string name) =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "providers", name));

    private static IReadOnlyList<FirmsHotspot> Contaminated() =>
        FirmsCsvParser.Parse(Fixture("firms-contaminated.csv"));

    private static FirmsProviderOptions Options(Action<FirmsProviderOptions>? configure = null)
    {
        var options = new FirmsProviderOptions { MinimumConfidence = 0, PersistentSourceDays = 0 };
        configure?.Invoke(options);
        return options;
    }

    [Fact]
    public void TheParserReadsWhetherADetectionWasOnTheNightSide()
    {
        var hotspots = Contaminated();

        // Two Ethiopian daytime crop burns; the Yemeni flare and the Ukrainian detections are all
        // on the night side.
        Assert.Equal(2, hotspots.Count(hotspot => !hotspot.IsNight));
        Assert.Equal(6, hotspots.Count(hotspot => hotspot.IsNight));
    }

    /// <summary>
    /// The flare mask. Three detections at one point in Yemen on three separate days are
    /// infrastructure burning continuously, not three events.
    /// </summary>
    [Fact]
    public void ALocationBurningOnSeveralSeparateDaysIsTreatedAsInfrastructure()
    {
        var result = FirmsConflictFilter.Apply(Contaminated(), Options(options => options.PersistentSourceDays = 3));

        Assert.Equal(3, result.PersistentSource);
        Assert.DoesNotContain(result.Kept, hotspot => Math.Abs(hotspot.Longitude - 48.783) < 0.01);
    }

    /// <summary>
    /// Distinct days rather than distinct detections, because a satellite passing twice in one night
    /// over a single genuine fire would otherwise look exactly like a permanent source.
    /// </summary>
    [Fact]
    public void TwoPassesOverOneNightAreNotMistakenForAPersistentSource()
    {
        var midnight = new DateTimeOffset(2026, 9, 9, 22, 0, 0, TimeSpan.Zero);

        var sameNight = new List<FirmsHotspot>
        {
            new(48.2790, 37.1760, 90, 350, 60, midnight, "N20", IsNight: true),
            new(48.2791, 37.1761, 90, 351, 62, midnight.AddMinutes(95), "N21", IsNight: true),
        };

        var result = FirmsConflictFilter.Apply(sameNight, Options(options => options.PersistentSourceDays = 2));

        Assert.Equal(0, result.PersistentSource);
        Assert.Equal(2, result.Kept.Count);
    }

    [Fact]
    public void ThePersistenceCheckCanBeTurnedOff()
    {
        var result = FirmsConflictFilter.Apply(Contaminated(), Options(options => options.PersistentSourceDays = 0));

        Assert.Equal(0, result.PersistentSource);
    }

    /// <summary>
    /// Night-only selection, which is the cheapest discriminator available against agricultural
    /// burning: it is overwhelmingly a daytime activity.
    /// </summary>
    [Fact]
    public void DaytimeDetectionsAreDroppedWhenNightOnlySelectionIsOn()
    {
        var result = FirmsConflictFilter.Apply(Contaminated(), Options(options => options.NightOnly = true));

        Assert.Equal(2, result.Daytime);
        Assert.All(result.Kept, hotspot => Assert.True(hotspot.IsNight));
    }

    [Fact]
    public void TheRadiativePowerFloorDropsWeakBurns()
    {
        var result = FirmsConflictFilter.Apply(
            Contaminated(),
            Options(options => options.MinimumRadiativePowerMegawatts = 10));

        Assert.All(result.Kept, hotspot => Assert.True(hotspot.RadiativePowerMegawatts >= 10));
        Assert.True(result.BelowPower > 0, "The fixture should contain weak burns for this to reject.");
    }

    /// <summary>
    /// A dataset that reports no power at all must not be read as reporting zero. Doing so would
    /// silently empty the feed for every deployment using one of those datasets, and an empty feed is
    /// the failure mode hardest to notice.
    /// </summary>
    [Fact]
    public void ADetectionWithNoReportedPowerIsKeptRatherThanReadAsZero()
    {
        var unstated = new List<FirmsHotspot>
        {
            new(48.2790, 37.1760, 90, 350, null, DateTimeOffset.UtcNow, "N20", IsNight: true),
        };

        var result = FirmsConflictFilter.Apply(
            unstated,
            Options(options => options.MinimumRadiativePowerMegawatts = 50));

        Assert.Single(result.Kept);
        Assert.Equal(0, result.BelowPower);
    }

    [Fact]
    public void EveryRejectionReasonIsCountedSeparately()
    {
        // A poll that yields nothing has said something useful if it also says why. The same empty
        // result with no breakdown is indistinguishable from a broken credential.
        var result = FirmsConflictFilter.Apply(Contaminated(), Options(options =>
        {
            options.MinimumConfidence = 50;
            options.NightOnly = true;
            options.MinimumRadiativePowerMegawatts = 10;
            options.PersistentSourceDays = 3;
        }));

        var accounted = result.Kept.Count
            + result.BelowConfidence
            + result.Daytime
            + result.BelowPower
            + result.PersistentSource;

        Assert.Equal(Contaminated().Count, accounted);
    }

    /// <summary>
    /// The combination the theatres would actually run, against a fixture built to look like what
    /// those theatres actually produce: a Yemeni flare burning three nights running, Ethiopian
    /// daytime crop burning, and three Ukrainian night detections of which one is too weak and one
    /// too low-confidence.
    /// </summary>
    [Fact]
    public void TheCombinedFiltersLeaveOnlyWhatIsWorthLookingAt()
    {
        var result = FirmsConflictFilter.Apply(Contaminated(), Options(options =>
        {
            options.MinimumConfidence = 50;
            options.NightOnly = true;
            options.MinimumRadiativePowerMegawatts = 10;
            options.PersistentSourceDays = 3;
        }));

        // Three flare detections, two daytime crop burns, one low-confidence and one weak burn, out
        // of eight. What is left is the single night-time high-power detection at Pokrovsk.
        Assert.Equal(3, result.PersistentSource);
        Assert.Equal(2, result.Daytime);
        Assert.Equal(1, result.BelowConfidence);
        Assert.Equal(1, result.BelowPower);

        var kept = Assert.Single(result.Kept);

        Assert.Equal(48.279, kept.Latitude, 3);
        Assert.Equal(37.176, kept.Longitude, 3);
        Assert.True(kept.IsNight);
    }

    [Fact]
    public void DetectionsAreReturnedNewestFirst()
    {
        var result = FirmsConflictFilter.Apply(Contaminated(), Options());

        Assert.Equal(
            result.Kept.OrderByDescending(hotspot => hotspot.AcquiredAt).Select(hotspot => hotspot.Identifier),
            result.Kept.Select(hotspot => hotspot.Identifier));
    }
}
