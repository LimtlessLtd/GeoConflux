using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Geopolitics.IntegrationTests;

/// <summary>
/// Drives the spatial endpoints through the real host, against the database the recorded stream
/// actually filled. The point is to check that the bounding-box predicate survives translation to
/// SQL: a rectangle filter that EF cannot translate, or that silently evaluates client-side, behaves
/// correctly in a unit test with an in-memory fake and fails or scans the table here.
/// </summary>
public sealed class SpatialEndpointTests
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };

    private const double BabElMandebLatitude = 12.585;
    private const double BabElMandebLongitude = 43.334;

    [Fact]
    public async Task IncidentsNearAPointComeBackWithMeasuredDistances()
    {
        using var factory = new PipelineFactory(runPipeline: true, runSources: true);
        using var client = factory.CreateClient();

        await WaitForIncidentsAsync(client);

        var response = await client.GetFromJsonAsync<NearbyResponse>(
            $"/api/spatial/incidents-near?lat={BabElMandebLatitude}&lon={BabElMandebLongitude}&radiusKm=150",
            Json);

        Assert.NotNull(response);
        Assert.NotEmpty(response.Incidents);

        // Nearest first, and every result genuinely inside the radius rather than merely inside the
        // rectangle used to find it.
        Assert.All(response.Incidents, incident => Assert.InRange(incident.DistanceKilometres, 0, 150));
        Assert.Equal(
            response.Incidents.Select(value => value.DistanceKilometres).Order(),
            response.Incidents.Select(value => value.DistanceKilometres));

        // The method travels with the answer, so a consumer is told how the distance was obtained.
        Assert.False(string.IsNullOrWhiteSpace(response.Method));
    }

    [Fact]
    public async Task ATightRadiusExcludesWhatAWideOneIncluded()
    {
        using var factory = new PipelineFactory(runPipeline: true, runSources: true);
        using var client = factory.CreateClient();

        await WaitForIncidentsAsync(client);

        var wide = await client.GetFromJsonAsync<NearbyResponse>(
            $"/api/spatial/incidents-near?lat={BabElMandebLatitude}&lon={BabElMandebLongitude}&radiusKm=500",
            Json);

        var tight = await client.GetFromJsonAsync<NearbyResponse>(
            $"/api/spatial/incidents-near?lat={BabElMandebLatitude}&lon={BabElMandebLongitude}&radiusKm=1",
            Json);

        Assert.NotNull(wide);
        Assert.NotNull(tight);

        // If the radius were being ignored — a filter evaluated client-side over everything, or a
        // predicate EF dropped — both calls would return the same set.
        Assert.True(tight.Count < wide.Count, $"Tight search returned {tight.Count}, wide returned {wide.Count}.");
    }

    [Fact]
    public async Task AnOutOfRangeCoordinateIsRejected()
    {
        using var factory = new PipelineFactory(runPipeline: false, runSources: false);
        using var client = factory.CreateClient();

        var response = await client.GetAsync(new Uri("/api/spatial/incidents-near?lat=999&lon=0", UriKind.Relative));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task ChokepointAnalysisReportsEveryWatchedPassage()
    {
        using var factory = new PipelineFactory(runPipeline: true, runSources: true);
        using var client = factory.CreateClient();

        await WaitForIncidentsAsync(client);

        var response = await client.GetFromJsonAsync<ChokepointResponse>(
            "/api/spatial/chokepoints?windowHours=720",
            Json);

        Assert.NotNull(response);
        Assert.NotEmpty(response.Chokepoints);

        // Quiet passages are reported too: "nothing recorded here" is an answer, and a list that
        // changed length between requests would be unreadable.
        Assert.Contains(response.Chokepoints, value => value.IncidentCount == 0);

        // The recorded stream places several reports at Bab-el-Mandeb, so that passage must be busy
        // and must sort ahead of the quiet ones.
        var strait = response.Chokepoints.Single(value => value.Name == "Bab-el-Mandeb");
        Assert.True(strait.IncidentCount > 0);
        Assert.Equal("Bab-el-Mandeb", response.Chokepoints[0].Name);

        // Proximity is stated as geography, not as an assessment, in the payload itself rather than
        // only in the UI that happens to render it.
        Assert.Contains("not an assessment", response.Notice, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Waits for the whole recorded stream to drain, not merely for the first incident to appear.
    /// <para>
    /// Waiting on "any result" raced the background processor: a comparison between a wide and a
    /// narrow search ran while a single observation had been stored, both searches returned that one
    /// incident, and the test failed for a reason unrelated to what it was checking.
    /// </para>
    /// </summary>
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

    private sealed record ObservationRow(Guid Id);

    private sealed record NearbyResponse(string Method, int Count, IReadOnlyList<NearbyIncidentResponse> Incidents);

    private sealed record NearbyIncidentResponse(IncidentSummary Incident, double DistanceKilometres);

    private sealed record IncidentSummary(Guid Id, string Title);

    private sealed record ChokepointResponse(string Notice, IReadOnlyList<ChokepointSummary> Chokepoints);

    private sealed record ChokepointSummary(string Name, int IncidentCount, double? NearestIncidentKilometres);
}
