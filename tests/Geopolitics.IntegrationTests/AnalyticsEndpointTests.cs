using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Geopolitics.IntegrationTests;

/// <summary>
/// Drives the analytics endpoint through the real host against the database the recorded stream
/// filled.
/// <para>
/// The reason these must be integration tests rather than unit tests is translation. Every breakdown
/// is a <c>GROUP BY</c> the database is meant to evaluate, and the grouping keys are enum properties
/// stored through value converters and an owned location type. A fake repository would answer all of
/// these happily while the real query threw, or — worse — silently fell back to evaluating the
/// grouping client-side over the whole table.
/// </para>
/// </summary>
public sealed class AnalyticsEndpointTests
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };

    [Fact]
    public async Task TheDefaultWindowSummarisesWhatThePipelineProduced()
    {
        using var factory = new PipelineFactory(runPipeline: true, runSources: true, scriptedStream: true);
        using var client = factory.CreateClient();

        await WaitForIncidentsAsync(client);

        var report = await client.GetFromJsonAsync<AnalyticsReportRow>("/api/analytics", Json);

        Assert.NotNull(report);
        Assert.Equal("24h", report.Window);
        Assert.True(report.IncidentCount > 0, "The recorded stream should have produced incidents.");
        Assert.True(report.ObservationCount > 0, "The recorded stream should have produced observations.");

        // Every recorded event occurs within seven hours of the run, so the 24-hour window holds all
        // of them and the breakdowns must agree with the headline total.
        Assert.Equal(report.IncidentCount, report.BySeverity.Sum(value => value.Count));
        Assert.Equal(report.IncidentCount, report.ByEventType.Sum(value => value.Count));
        Assert.Equal(report.ObservationCount, report.BySourceKind.Sum(value => value.Count));
        Assert.Equal(report.ObservationCount, report.ByObservationStatus.Sum(value => value.Count));

        // The timeseries is drawn from the same sample as the score, so it must agree too.
        Assert.Equal(24, report.Timeseries.Count);
        Assert.Equal(report.IncidentCount, report.Timeseries.Sum(bucket => bucket.Count));

        // Correlation is the point of the pipeline; the recorded stream is built to exercise it.
        Assert.True(report.CorrelatedIncidentCount > 0, "Multi-source correlation should have occurred.");
        Assert.InRange(report.CorrelatedIncidentCount, 0, report.IncidentCount);
    }

    [Fact]
    public async Task RegionalActivityIsGroupedByResolvedPlaceRatherThanCollapsedIntoOneBucket()
    {
        using var factory = new PipelineFactory(runPipeline: true, runSources: true, scriptedStream: true);
        using var client = factory.CreateClient();

        await WaitForIncidentsAsync(client);

        var report = await client.GetFromJsonAsync<AnalyticsReportRow>("/api/analytics?window=7d", Json);

        Assert.NotNull(report);
        Assert.NotEmpty(report.ByRegion);

        // Most of the recorded stream is maritime, so the gazetteer resolves places with no country
        // code. Grouping on the code alone would put all of them in one row named after whichever
        // sorted first, which is the bug this asserts against.
        Assert.All(report.ByRegion, region => Assert.False(string.IsNullOrWhiteSpace(region.Name)));
        Assert.Equal(
            report.ByRegion.Select(value => value.Name).Distinct().Count(),
            report.ByRegion.Count);

        // Busiest first.
        Assert.Equal(
            report.ByRegion.Select(value => value.Count).OrderDescending(),
            report.ByRegion.Select(value => value.Count));
    }

    [Fact]
    public async Task TheActivityScoreArrivesWithItsFormulaAndItsCaveat()
    {
        using var factory = new PipelineFactory(runPipeline: true, runSources: true, scriptedStream: true);
        using var client = factory.CreateClient();

        await WaitForIncidentsAsync(client);

        var report = await client.GetFromJsonAsync<AnalyticsReportRow>("/api/analytics", Json);

        Assert.NotNull(report);
        Assert.InRange(report.Score.Value, 0, 100);
        Assert.True(report.Score.IncidentsScored > 0);
        Assert.False(report.Score.Truncated, "The recorded stream is far below the sample cap.");

        // A number without its definition is the failure mode this whole feature has to avoid.
        Assert.Contains("score =", report.Score.Formula, StringComparison.Ordinal);
        Assert.Contains("heuristic", report.Score.Notice, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(5, report.Score.Components.Count);
    }

    [Fact]
    public async Task MaritimeActivityCountsProximitiesRatherThanClaimingDistinctIncidents()
    {
        using var factory = new PipelineFactory(runPipeline: true, runSources: true, scriptedStream: true);
        using var client = factory.CreateClient();

        await WaitForIncidentsAsync(client);

        var report = await client.GetFromJsonAsync<AnalyticsReportRow>("/api/analytics?window=7d", Json);

        Assert.NotNull(report);
        Assert.True(report.Maritime.ChokepointsWatched > 0);
        Assert.True(report.Maritime.ChokepointsWithActivity > 0, "The recorded stream places reports near watched passages.");
        Assert.NotNull(report.Maritime.Busiest);
        Assert.Equal("Bab-el-Mandeb", report.Maritime.Busiest.Name);

        // Every watched passage is listed, quiet ones included, and the total is the sum of the per
        // passage counts — which is why it is named for proximities rather than incidents.
        Assert.Equal(report.Maritime.ChokepointsWatched, report.Maritime.Chokepoints.Count);
        Assert.Equal(report.Maritime.ProximityCount, report.Maritime.Chokepoints.Sum(value => value.IncidentCount));
    }

    [Fact]
    public async Task AWiderWindowNeverReportsFewerIncidentsThanANarrowerOne()
    {
        using var factory = new PipelineFactory(runPipeline: true, runSources: true, scriptedStream: true);
        using var client = factory.CreateClient();

        await WaitForIncidentsAsync(client);

        var day = await client.GetFromJsonAsync<AnalyticsReportRow>("/api/analytics?window=24h", Json);
        var quarter = await client.GetFromJsonAsync<AnalyticsReportRow>("/api/analytics?window=90d", Json);

        Assert.NotNull(day);
        Assert.NotNull(quarter);
        Assert.True(quarter.IncidentCount >= day.IncidentCount);

        // Bucket resolution changes with the window so the series stays readable at every zoom.
        Assert.Equal(24, day.Timeseries.Count);
        Assert.Equal(90, quarter.Timeseries.Count);
    }

    [Fact]
    public async Task AnUnsupportedWindowIsRejectedRatherThanSilentlyWidened()
    {
        using var factory = new PipelineFactory(runPipeline: false, runSources: false);
        using var client = factory.CreateClient();

        var response = await client.GetAsync(new Uri("/api/analytics?window=1y", UriKind.Relative));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task TheSupportedWindowsAreDiscoverable()
    {
        using var factory = new PipelineFactory(runPipeline: false, runSources: false);
        using var client = factory.CreateClient();

        var windows = await client.GetFromJsonAsync<List<WindowRow>>("/api/analytics/windows", Json);

        Assert.NotNull(windows);
        Assert.Equal(["24h", "7d", "30d", "90d"], windows.Select(value => value.Token));
    }

    [Fact]
    public async Task AnEmptyDatabaseAnswersWithZerosRatherThanFailing()
    {
        // Seeding off as well as ingestion, so the database really is empty. The illustrative
        // records a first run normally gets would otherwise make this a test of the seeder.
        using var factory = new PipelineFactory(
            runPipeline: false,
            runSources: false,
            settings: null);
        using var client = factory.CreateClient();

        var report = await client.GetFromJsonAsync<AnalyticsReportRow>("/api/analytics", Json);

        Assert.NotNull(report);
        Assert.Equal(0, report.IncidentCount);
        Assert.Equal(0, report.Score.Value);

        // The series is still full length: a chart with no points and a chart with no data are
        // different things, and only one of them is honest about the period it covers.
        Assert.Equal(24, report.Timeseries.Count);
    }

    private static async Task WaitForIncidentsAsync(HttpClient client)
    {
        const int RecordedObservations = 11;

        for (var attempt = 0; attempt < 200; attempt++)
        {
            var observations = await client.GetFromJsonAsync<List<ObservationRow>>("/api/observations?take=200", Json);

            if (observations is { Count: >= RecordedObservations })
            {
                return;
            }

            await Task.Delay(50);
        }

        throw new InvalidOperationException("The recorded stream did not finish processing within the timeout.");
    }

    private sealed record AnalyticsReportRow(
        string Window,
        DateTimeOffset From,
        DateTimeOffset To,
        int IncidentCount,
        int CorrelatedIncidentCount,
        int ObservationCount,
        int SatelliteObservationCount,
        List<CategoryRow> BySeverity,
        List<CategoryRow> ByEventType,
        List<RegionRow> ByRegion,
        List<CategoryRow> BySourceKind,
        List<CategoryRow> ByObservationStatus,
        List<BucketRow> Timeseries,
        MaritimeRow Maritime,
        ScoreRow Score,
        bool Truncated);

    private sealed record ObservationRow(Guid Id);

    private sealed record CategoryRow(string Category, int Count);

    private sealed record RegionRow(string? CountryCode, string Name, int Count);

    private sealed record BucketRow(DateTimeOffset Start, int Count, int Elevated);

    private sealed record MaritimeRow(
        int ChokepointsWatched,
        int ChokepointsWithActivity,
        int ProximityCount,
        ChokepointRow? Busiest,
        List<ChokepointRow> Chokepoints);

    private sealed record ChokepointRow(string Name, int IncidentCount, double? NearestKilometres);

    private sealed record ScoreRow(
        double Value,
        string Band,
        double WeightedTotal,
        double WeightedPerDay,
        int IncidentsScored,
        bool Truncated,
        List<ComponentRow> Components,
        string Formula,
        string Notice);

    private sealed record ComponentRow(string Label, string Description, double Value);

    private sealed record WindowRow(string Token, string Label, double Hours, int Buckets);
}
