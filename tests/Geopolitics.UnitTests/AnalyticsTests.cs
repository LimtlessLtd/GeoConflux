using Geopolitics.Application.Abstractions;
using Geopolitics.Application.Analytics;
using Geopolitics.Domain;
using Microsoft.Extensions.Time.Testing;

namespace Geopolitics.UnitTests;

/// <summary>
/// Covers the analytics heuristic and the assembly around it.
/// <para>
/// The score is the part of this project most able to mislead, because it produces a confident-looking
/// number from soft inputs. These tests pin the properties that make it defensible — bounded, monotone
/// in each factor, decaying with age, rate-normalised across windows — rather than pinning the output
/// value, which would only assert that the constants have not been retyped.
/// </para>
/// </summary>
public sealed class ActivityScoreTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 11, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void AnEmptyWindowScoresZero()
    {
        var score = Score([]);

        Assert.Equal(0, score.Value);
        Assert.Equal("quiet", score.Band);
        Assert.Equal(0, score.IncidentsScored);
    }

    [Fact]
    public void TheScoreStaysWithinItsStatedRange()
    {
        // Far more activity than any real window, to confirm the saturating form is what bounds the
        // value rather than a clamp applied after the fact.
        var flood = Enumerable
            .Range(0, 2_000)
            .Select(_ => new IncidentScoreInput(Severity.Critical, Now, 50, 1.0))
            .ToArray();

        var score = Score(flood);

        Assert.InRange(score.Value, 0, 100);
        Assert.Equal("high", score.Band);
    }

    [Fact]
    public void SeverityRaisesTheScore()
    {
        var low = Score([new IncidentScoreInput(Severity.Low, Now, 1, 0.6)]);
        var critical = Score([new IncidentScoreInput(Severity.Critical, Now, 1, 0.6)]);

        Assert.True(critical.Value > low.Value, $"Critical scored {critical.Value}, Low scored {low.Value}.");
    }

    [Fact]
    public void AgeLowersTheScore()
    {
        var fresh = Score([new IncidentScoreInput(Severity.High, Now, 1, 0.6)]);
        var stale = Score([new IncidentScoreInput(Severity.High, Now.AddHours(-18), 1, 0.6)]);

        Assert.True(stale.Value < fresh.Value, $"Stale scored {stale.Value}, fresh scored {fresh.Value}.");
    }

    [Fact]
    public void CorroborationRaisesTheScoreButCannotRunAway()
    {
        var single = Score([new IncidentScoreInput(Severity.Medium, Now, 1, 0.6)]);
        var corroborated = Score([new IncidentScoreInput(Severity.Medium, Now, 4, 0.6)]);
        var syndicated = Score([new IncidentScoreInput(Severity.Medium, Now, 5_000, 0.6)]);

        Assert.True(corroborated.Value > single.Value);

        // The cap is the point: one story republished by every outlet on earth must not outweigh a
        // genuinely multi-sourced event by an unbounded margin.
        var corroborationCeiling = Score(
        [
            new IncidentScoreInput(Severity.Medium, Now, 64, 0.6),
        ]);

        Assert.Equal(corroborationCeiling.WeightedTotal, syndicated.WeightedTotal, 6);
    }

    [Fact]
    public void ConfidenceScalesTheContributionWithoutErasingIt()
    {
        var confident = Score([new IncidentScoreInput(Severity.High, Now, 1, 1.0)]);
        var doubtful = Score([new IncidentScoreInput(Severity.High, Now, 1, 0.0)]);

        Assert.True(doubtful.Value < confident.Value);

        // An unconfident classification still describes a report that arrived, so it must not
        // contribute nothing at all.
        Assert.True(doubtful.Value > 0);
    }

    [Fact]
    public void TheSameActivityRateScoresAboutTheSameInEveryWindow()
    {
        // One incident per hour in each window, so the underlying rate is identical and only the
        // period differs. This is the property that makes the four windows comparable: without the
        // division by window length, a 90-day view would always score higher than a 24-hour view of
        // exactly the same activity, and the tabs would look like an escalation.
        var day = RateSample(AnalyticsWindow.Last24Hours);
        var week = RateSample(AnalyticsWindow.Last7Days);

        var dayScore = ActivityScoreCalculator.Calculate(new IncidentScoreSample(day, false), AnalyticsWindow.Last24Hours, Now);
        var weekScore = ActivityScoreCalculator.Calculate(new IncidentScoreSample(week, false), AnalyticsWindow.Last7Days, Now);

        // Close, not equal. The decay is summed over discrete incidents rather than integrated, and
        // an hourly step is a coarser sample of a six-hour half-life than of a forty-two-hour one, so
        // a few percent of discretisation error survives. Asserting exact equality would be asserting
        // something the calculation does not claim.
        Assert.InRange(Math.Abs(dayScore.Value - weekScore.Value), 0, 3);
    }

    [Fact]
    public void TheResultCarriesItsOwnFormulaAndCaveat()
    {
        var score = Score([new IncidentScoreInput(Severity.Medium, Now, 2, 0.5)]);

        // Both travel with the payload rather than living only in the UI, so an API consumer cannot
        // receive the number without receiving what it means.
        Assert.Contains("score =", score.Formula, StringComparison.Ordinal);
        Assert.Contains("not an objective measure", score.Notice, StringComparison.OrdinalIgnoreCase);

        // Every listed component is reported, so the number can be taken apart.
        Assert.Equal(
            ["Severity", "Recency", "Corroboration", "Confidence", "Frequency"],
            score.Components.Select(value => value.Label));
    }

    [Fact]
    public void TruncationIsCarriedThroughToTheResult()
    {
        var score = ActivityScoreCalculator.Calculate(
            new IncidentScoreSample([new IncidentScoreInput(Severity.Low, Now, 1, 0.5)], Truncated: true),
            AnalyticsWindow.Last30Days,
            Now);

        Assert.True(score.Truncated);
    }

    /// <summary>Incidents spaced one hour apart filling the given window, newest first.</summary>
    private static IncidentScoreInput[] RateSample(AnalyticsWindow window) =>
        [.. Enumerable
            .Range(0, (int)window.Duration.TotalHours)
            .Select(hour => new IncidentScoreInput(Severity.Medium, Now.AddHours(-hour), 1, 0.6))];

    private static ActivityScore Score(IReadOnlyList<IncidentScoreInput> inputs) =>
        ActivityScoreCalculator.Calculate(new IncidentScoreSample(inputs, false), AnalyticsWindow.Last24Hours, Now);
}

public sealed class AnalyticsWindowTests
{
    [Theory]
    [InlineData("24h", 24)]
    [InlineData("7d", 168)]
    [InlineData("30d", 720)]
    [InlineData("90d", 2160)]
    [InlineData("7D", 168)]
    public void SupportedTokensParse(string token, double expectedHours)
    {
        Assert.True(AnalyticsWindow.TryParse(token, out var window));
        Assert.Equal(expectedHours, window.Duration.TotalHours);
    }

    [Theory]
    [InlineData("1y")]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("36h")]
    public void UnsupportedTokensAreRejectedRatherThanApproximated(string? token)
    {
        Assert.False(AnalyticsWindow.TryParse(token, out var window));

        // Parse still yields something usable, but the caller was told the token was not recognised
        // so an API can refuse rather than silently answer a different question.
        Assert.Equal(AnalyticsWindow.Last24Hours, window);
    }

    [Fact]
    public void EveryWindowProducesAReadableNumberOfBuckets()
    {
        Assert.All(AnalyticsWindow.All, window => Assert.InRange(window.BucketCount, 24, 90));
    }
}

/// <summary>
/// Exercises the assembly step over a scripted repository, so bucketing and shaping are tested
/// without a database in the way.
/// </summary>
public sealed class AnalyticsServiceTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 11, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task TheTimeseriesCoversTheWholeWindowIncludingQuietBuckets()
    {
        var repository = new ScriptedAnalyticsRepository
        {
            Sample = new IncidentScoreSample(
                [
                    new IncidentScoreInput(Severity.High, Now.AddMinutes(-10), 2, 0.7),
                    new IncidentScoreInput(Severity.Low, Now.AddHours(-23), 1, 0.3),
                ],
                false),
        };

        var report = await Build(repository, AnalyticsWindow.Last24Hours);

        Assert.Equal(24, report.Timeseries.Count);
        Assert.Equal(2, report.Timeseries.Sum(bucket => bucket.Count));

        // Empty buckets are present rather than omitted; a chart drawn from only the busy ones would
        // compress the quiet hours out of existence.
        Assert.Contains(report.Timeseries, bucket => bucket.Count == 0);

        // Oldest first, and the newest incident lands in the final bucket.
        Assert.Equal(report.Timeseries.Select(b => b.Start).Order(), report.Timeseries.Select(b => b.Start));
        Assert.Equal(1, report.Timeseries[^1].Count);

        // High and Critical are counted separately, so volume and seriousness stay distinguishable.
        Assert.Equal(1, report.Timeseries.Sum(bucket => bucket.Elevated));
    }

    [Fact]
    public async Task TheWindowBoundaryIsHalfOpenSoConsecutivePeriodsDoNotOverlap()
    {
        // One incident exactly at the start of the window and one exactly at its end. The documented
        // interval is [from, to), so the first belongs to this period and the second belongs to the
        // next one. Counting both would double-count every incident that sits on a boundary between
        // two windows a reader compares.
        var repository = new ScriptedAnalyticsRepository
        {
            Sample = new IncidentScoreSample(
                [
                    new IncidentScoreInput(Severity.Medium, Now, 1, 0.5),
                    new IncidentScoreInput(Severity.Medium, Now.AddHours(-24), 1, 0.5),
                ],
                false),
        };

        var report = await Build(repository, AnalyticsWindow.Last24Hours);

        Assert.Equal(1, report.Timeseries.Sum(bucket => bucket.Count));
        Assert.Equal(1, report.Timeseries[0].Count);
        Assert.Equal(0, report.Timeseries[^1].Count);
    }

    [Fact]
    public async Task SeverityRowsKeepARankOrderRatherThanAFrequencyOrder()
    {
        var repository = new ScriptedAnalyticsRepository
        {
            BySeverity =
            [
                new CategoryCount(nameof(Severity.Low), 40),
                new CategoryCount(nameof(Severity.Critical), 1),
                new CategoryCount(nameof(Severity.Medium), 12),
            ],
        };

        var report = await Build(repository, AnalyticsWindow.Last7Days);

        Assert.Equal(
            [nameof(Severity.Critical), nameof(Severity.Medium), nameof(Severity.Low)],
            report.BySeverity.Select(value => value.Category));
    }

    [Fact]
    public async Task SatelliteActivityIsReadFromTheSourceKindBreakdown()
    {
        var repository = new ScriptedAnalyticsRepository
        {
            ByKind =
            [
                new CategoryCount(nameof(ObservationKind.News), 9),
                new CategoryCount(nameof(ObservationKind.Satellite), 4),
            ],
        };

        var report = await Build(repository, AnalyticsWindow.Last24Hours);

        Assert.Equal(4, report.SatelliteObservationCount);
        Assert.Equal(13, report.ObservationCount);
    }

    [Fact]
    public async Task EveryBreakdownDescribesTheSamePeriod()
    {
        var repository = new ScriptedAnalyticsRepository();
        var report = await Build(repository, AnalyticsWindow.Last30Days);

        // One clock reading for the whole report. Each query resolving "now" independently would
        // give the total and its own breakdowns slightly different periods.
        Assert.All(repository.RequestedWindows, window =>
        {
            Assert.Equal(report.From, window.Start);
            Assert.Equal(report.To, window.End);
        });

        Assert.Equal(TimeSpan.FromDays(30), report.To - report.From);
    }

    private static Task<AnalyticsReport> Build(ScriptedAnalyticsRepository repository, AnalyticsWindow window)
    {
        var time = new FakeTimeProvider(Now);
        var service = new AnalyticsService(repository, new EmptySpatialQueryService(), time);
        return service.BuildAsync(window, CancellationToken.None);
    }
}

/// <summary>A repository that returns whatever the test sets and records the periods it was asked for.</summary>
internal sealed class ScriptedAnalyticsRepository : IAnalyticsRepository
{
    private readonly List<(DateTimeOffset Start, DateTimeOffset End)> requested = [];

    public IReadOnlyList<(DateTimeOffset Start, DateTimeOffset End)> RequestedWindows => requested;

    public int IncidentCount { get; init; }

    public int CorrelatedCount { get; init; }

    public IReadOnlyList<CategoryCount> BySeverity { get; init; } = [];

    public IReadOnlyList<CategoryCount> ByEventType { get; init; } = [];

    public IReadOnlyList<RegionCount> ByRegion { get; init; } = [];

    public IReadOnlyList<CategoryCount> ByKind { get; init; } = [];

    public IReadOnlyList<CategoryCount> ByStatus { get; init; } = [];

    public IncidentScoreSample Sample { get; init; } = new([], false);

    public Task<int> CountIncidentsAsync(DateTimeOffset windowStart, DateTimeOffset windowEnd, CancellationToken cancellationToken) =>
        Record(windowStart, windowEnd, IncidentCount);

    public Task<int> CountCorrelatedIncidentsAsync(DateTimeOffset windowStart, DateTimeOffset windowEnd, CancellationToken cancellationToken) =>
        Record(windowStart, windowEnd, CorrelatedCount);

    public Task<IReadOnlyList<CategoryCount>> CountIncidentsBySeverityAsync(DateTimeOffset windowStart, DateTimeOffset windowEnd, CancellationToken cancellationToken) =>
        Record(windowStart, windowEnd, BySeverity);

    public Task<IReadOnlyList<CategoryCount>> CountIncidentsByEventTypeAsync(DateTimeOffset windowStart, DateTimeOffset windowEnd, CancellationToken cancellationToken) =>
        Record(windowStart, windowEnd, ByEventType);

    public Task<IReadOnlyList<RegionCount>> CountIncidentsByRegionAsync(DateTimeOffset windowStart, DateTimeOffset windowEnd, int take, CancellationToken cancellationToken) =>
        Record(windowStart, windowEnd, ByRegion);

    public Task<IReadOnlyList<CategoryCount>> CountObservationsByKindAsync(DateTimeOffset windowStart, DateTimeOffset windowEnd, CancellationToken cancellationToken) =>
        Record(windowStart, windowEnd, ByKind);

    public Task<IReadOnlyList<CategoryCount>> CountObservationsByStatusAsync(DateTimeOffset windowStart, DateTimeOffset windowEnd, CancellationToken cancellationToken) =>
        Record(windowStart, windowEnd, ByStatus);

    public Task<IncidentScoreSample> SampleIncidentsAsync(DateTimeOffset windowStart, DateTimeOffset windowEnd, int take, CancellationToken cancellationToken) =>
        Record(windowStart, windowEnd, Sample);

    private Task<T> Record<T>(DateTimeOffset start, DateTimeOffset end, T value)
    {
        requested.Add((start, end));
        return Task.FromResult(value);
    }
}

/// <summary>A spatial service with nothing to report, so maritime data cannot influence these assertions.</summary>
internal sealed class EmptySpatialQueryService : ISpatialQueryService
{
    public string Method => "test";

    public Task<IReadOnlyList<NearbyIncident>> FindIncidentsNearAsync(
        double latitude,
        double longitude,
        double radiusKilometres,
        int take,
        CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<NearbyIncident>>([]);

    public Task<IReadOnlyList<ChokepointActivity>> AnalyseChokepointsAsync(TimeSpan window, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<ChokepointActivity>>([]);
}
