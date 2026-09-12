using Geopolitics.Application.Abstractions;
using Geopolitics.Application.Spatial;
using Geopolitics.Domain;
using Geopolitics.UnitTests.Fakes;
using Microsoft.Extensions.Time.Testing;

namespace Geopolitics.UnitTests;

public sealed class GeoBoundingBoxTests
{
    [Fact]
    public void TheBoxContainsEveryPointTheCircleDoes()
    {
        // The contract the whole two-stage search rests on. The rectangle may admit points the
        // circle rejects; it must never exclude one the circle would have accepted, because those
        // rows never reach the exact distance check and would be silently lost.
        var centre = new GeoLocation("centre", null, 12.585, 43.334);
        var box = GeoBoundingBox.FromRadius(centre.Latitude, centre.Longitude, 100);

        for (var bearing = 0; bearing < 360; bearing += 5)
        {
            var (latitude, longitude) = Offset(centre, 99.5, bearing);

            Assert.True(
                box.Contains(latitude, longitude),
                $"Bearing {bearing}° at 99.5 km fell outside the box ({latitude:F4}, {longitude:F4}).");
        }
    }

    [Fact]
    public void TheBoxIsWiderInLongitudeFurtherFromTheEquator()
    {
        // Meridians converge, so a fixed distance spans more degrees of longitude at high latitude.
        // A box that ignored this would be too narrow near the poles and would lose rows.
        var equator = GeoBoundingBox.FromRadius(0, 0, 200);
        var northern = GeoBoundingBox.FromRadius(60, 0, 200);

        Assert.True(northern.East - northern.West > equator.East - equator.West);
    }

    [Fact]
    public void ABoxSpanningTheAntimeridianIsTwoIntervals()
    {
        var box = GeoBoundingBox.FromRadius(0, 179.5, 200);

        Assert.True(box.CrossesAntimeridian);

        // West is greater than East precisely because the range runs [West, 180] then [-180, East].
        Assert.True(box.West > box.East);

        Assert.True(box.Contains(0, 179.9));
        Assert.True(box.Contains(0, -179.9));
        Assert.False(box.Contains(0, 0));
    }

    [Fact]
    public void ACircleReachingAPoleSpansEveryLongitude()
    {
        // Not a degenerate case: a circle that reaches the pole genuinely does contain every
        // meridian, and pretending otherwise would exclude real rows.
        var box = GeoBoundingBox.FromRadius(89.9, 0, 500);

        Assert.Equal(-180, box.West);
        Assert.Equal(180, box.East);
        Assert.False(box.CrossesAntimeridian);
        Assert.True(box.Contains(89.95, -170));
    }

    [Theory]
    [InlineData(91, 0, 10)]
    [InlineData(0, 181, 10)]
    [InlineData(0, 0, 0)]
    [InlineData(0, 0, -5)]
    public void InvalidInputsAreRejected(double latitude, double longitude, double radius) =>
        Assert.Throws<DomainException>(() => GeoBoundingBox.FromRadius(latitude, longitude, radius));

    /// <summary>Moves a point a given distance along a bearing, for generating known-good test points.</summary>
    private static (double Latitude, double Longitude) Offset(GeoLocation origin, double distanceKm, double bearingDegrees)
    {
        const double earthRadiusKm = 6371.0088;
        var angular = distanceKm / earthRadiusKm;
        var bearing = bearingDegrees * Math.PI / 180;
        var latitude = origin.Latitude * Math.PI / 180;
        var longitude = origin.Longitude * Math.PI / 180;

        var destinationLatitude = Math.Asin(
            (Math.Sin(latitude) * Math.Cos(angular))
            + (Math.Cos(latitude) * Math.Sin(angular) * Math.Cos(bearing)));

        var destinationLongitude = longitude + Math.Atan2(
            Math.Sin(bearing) * Math.Sin(angular) * Math.Cos(latitude),
            Math.Cos(angular) - (Math.Sin(latitude) * Math.Sin(destinationLatitude)));

        return (destinationLatitude * 180 / Math.PI, destinationLongitude * 180 / Math.PI);
    }
}

public sealed class SpatialQueryServiceTests
{
    private static readonly DateTimeOffset Now = new(2026, 5, 1, 12, 0, 0, TimeSpan.Zero);
    private static readonly GeoLocation BabElMandeb = new("Bab-el-Mandeb", "DJ", 12.585, 43.334);

    [Fact]
    public async Task OnlyIncidentsInsideTheCircleAreReturned()
    {
        var repository = new FakeIncidentRepository();

        // 128 km from the centre, and inside the bounding box for a 100 km search: the box spans
        // ±0.90° latitude and ±0.92° longitude, and this point sits at +0.80° and +0.85°. It is the
        // row that proves the exact distance check does real work rather than rubber-stamping
        // whatever the rectangle admitted — a rectangle's corner reaches about 1.4 times the radius.
        repository.Seed(Incident("corner", 13.385, 44.184));
        repository.Seed(Incident("close", BabElMandeb.Latitude, BabElMandeb.Longitude));

        var results = await Build(repository).FindIncidentsNearAsync(
            BabElMandeb.Latitude,
            BabElMandeb.Longitude,
            radiusKilometres: 100,
            take: 50,
            CancellationToken.None);

        var match = Assert.Single(results);
        Assert.Equal("close", match.Incident.Title);
    }

    [Fact]
    public async Task ResultsComeBackNearestFirst()
    {
        var repository = new FakeIncidentRepository();
        repository.Seed(Incident("far", 13.2, 43.334));
        repository.Seed(Incident("near", 12.6, 43.334));
        repository.Seed(Incident("middle", 12.9, 43.334));

        var results = await Build(repository).FindIncidentsNearAsync(
            BabElMandeb.Latitude,
            BabElMandeb.Longitude,
            radiusKilometres: 200,
            take: 50,
            CancellationToken.None);

        Assert.Equal(["near", "middle", "far"], results.Select(value => value.Incident.Title));
        Assert.True(results[0].DistanceKilometres < results[1].DistanceKilometres);
    }

    [Fact]
    public async Task AnIncidentWithoutCoordinatesIsNeverPlacedNearAnything()
    {
        var repository = new FakeIncidentRepository();
        repository.Seed(Incident("unplaced", location: null));

        var results = await Build(repository).FindIncidentsNearAsync(
            BabElMandeb.Latitude,
            BabElMandeb.Longitude,
            radiusKilometres: 5000,
            take: 50,
            CancellationToken.None);

        // An unresolved location means "we do not know where", not "at the origin".
        Assert.Empty(results);
    }

    [Fact]
    public async Task ChokepointAnalysisCountsWhatIsInsideEachWatchRadius()
    {
        var repository = new FakeIncidentRepository();
        repository.Seed(Incident("at the strait", BabElMandeb.Latitude, BabElMandeb.Longitude));
        repository.Seed(Incident("nearby", 12.61, 43.41));
        repository.Seed(Incident("elsewhere", 51.507, -0.128));

        var results = await Build(repository).AnalyseChokepointsAsync(TimeSpan.FromDays(30), CancellationToken.None);

        var strait = results.Single(value => value.Name == "Bab-el-Mandeb");
        Assert.Equal(2, strait.IncidentCount);
        Assert.Equal(0, strait.NearestIncidentKilometres);
        Assert.DoesNotContain(strait.Incidents, value => value.Incident.Title == "elsewhere");
    }

    [Fact]
    public async Task QuietChokepointsAreStillReported()
    {
        var repository = new FakeIncidentRepository();
        repository.Seed(Incident("at the strait", BabElMandeb.Latitude, BabElMandeb.Longitude));

        var results = await Build(repository).AnalyseChokepointsAsync(TimeSpan.FromDays(30), CancellationToken.None);

        // "Nothing recorded here" is an answer. Dropping quiet entries would make the watch list
        // change length between requests and hide the fact that a passage was checked at all.
        Assert.Contains(results, value => value is { Name: "Panama Canal", IncidentCount: 0 });
        Assert.Null(results.Single(value => value.Name == "Panama Canal").NearestIncidentKilometres);
    }

    [Fact]
    public async Task BusiestChokepointsComeFirst()
    {
        var repository = new FakeIncidentRepository();
        repository.Seed(Incident("hormuz", 26.567, 56.250));
        repository.Seed(Incident("mandeb one", BabElMandeb.Latitude, BabElMandeb.Longitude));
        repository.Seed(Incident("mandeb two", 12.6, 43.35));

        var results = await Build(repository).AnalyseChokepointsAsync(TimeSpan.FromDays(30), CancellationToken.None);

        Assert.Equal("Bab-el-Mandeb", results[0].Name);
        Assert.Equal(2, results[0].IncidentCount);
    }

    [Fact]
    public async Task IncidentsOlderThanTheWindowAreExcluded()
    {
        var repository = new FakeIncidentRepository();
        repository.Seed(Incident("recent", BabElMandeb.Latitude, BabElMandeb.Longitude, Now.AddHours(-2)));
        repository.Seed(Incident("stale", BabElMandeb.Latitude, BabElMandeb.Longitude, Now.AddDays(-40)));

        var results = await Build(repository).AnalyseChokepointsAsync(TimeSpan.FromHours(24), CancellationToken.None);

        var strait = results.Single(value => value.Name == "Bab-el-Mandeb");
        Assert.Equal(1, strait.IncidentCount);
        Assert.Equal("recent", strait.Incidents[0].Incident.Title);
    }

    [Fact]
    public async Task SeverityIsBrokenDownHighestFirst()
    {
        var repository = new FakeIncidentRepository();
        repository.Seed(Incident("low", BabElMandeb.Latitude, BabElMandeb.Longitude, severity: Severity.Low));
        repository.Seed(Incident("critical", 12.6, 43.35, severity: Severity.Critical));
        repository.Seed(Incident("also critical", 12.59, 43.34, severity: Severity.Critical));

        var results = await Build(repository).AnalyseChokepointsAsync(TimeSpan.FromDays(30), CancellationToken.None);
        var strait = results.Single(value => value.Name == "Bab-el-Mandeb");

        Assert.Equal("Critical", strait.SeverityCounts[0].Severity);
        Assert.Equal(2, strait.SeverityCounts[0].Count);
    }

    [Fact]
    public void TheServiceStatesHowItComputedItsDistances() =>
        // Carried into the API payload and the published snapshot, so a reader is told the method
        // rather than left to assume a precision the backend does not have.
        Assert.Equal(
            "bounding-box prefilter, great-circle distance",
            Build(new FakeIncidentRepository()).Method);

    private static SpatialQueryService Build(FakeIncidentRepository repository) =>
        new(repository, new TestChokepointCatalogue(), new FakeTimeProvider(Now));

    private static GeopoliticalIncident Incident(
        string title,
        double latitude,
        double longitude,
        DateTimeOffset? occurredAt = null,
        Severity severity = Severity.Medium) =>
        Incident(title, new GeoLocation(title, null, latitude, longitude), occurredAt, severity);

    private static GeopoliticalIncident Incident(
        string title,
        GeoLocation? location,
        DateTimeOffset? occurredAt = null,
        Severity severity = Severity.Medium) =>
        new(
            Guid.NewGuid(),
            title,
            "Summary.",
            EventType.MaritimeIncident,
            severity,
            occurredAt ?? Now.AddHours(-1),
            location,
            ObservationProvenance.Polled,
            occurredAt ?? Now.AddHours(-1));

    /// <summary>
    /// A trimmed catalogue. Using the production one would tie these assertions to the exact
    /// contents of a curated list that is expected to change.
    /// </summary>
    private sealed class TestChokepointCatalogue : IChokepointCatalogue
    {
        public IReadOnlyList<MaritimeChokepoint> Chokepoints { get; } =
        [
            new("Bab-el-Mandeb", "Southern gate of the Red Sea.", 12.585, 43.334, 120),
            new("Strait of Hormuz", "Only sea route out of the Persian Gulf.", 26.567, 56.250, 120),
            new("Panama Canal", "Lock canal linking the Atlantic and Pacific.", 9.080, -79.680, 60),
        ];
    }
}
