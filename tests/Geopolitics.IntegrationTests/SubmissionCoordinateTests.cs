using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace Geopolitics.IntegrationTests;

/// <summary>
/// The open write path may not place a pin.
/// <para>
/// <c>POST /api/observations</c> used to accept a latitude and longitude and the resolver honoured
/// them as source-provided: an exact position at 0.95 confidence, supplied by an anonymous caller,
/// in the configuration this repository ships. ADR 005 exists to prevent precisely that, and the one
/// door it was not enforced at was the one anybody on the network could open.
/// </para>
/// <para>
/// Exercised against the real API rather than the DTO, because what matters is not that the property
/// was deleted but that sending it anyway achieves nothing.
/// </para>
/// </summary>
public sealed class SubmissionCoordinateTests
{
    [Fact]
    public async Task CoordinatesSentToTheOpenEndpointArePlainlyIgnored()
    {
        using var factory = new PipelineFactory(runPipeline: true, runSources: false);
        using var client = factory.CreateClient();

        // Somewhere unmistakable and nowhere near the place actually named.
        using var response = await client.PostAsJsonAsync("/api/observations", new
        {
            sourceName = "coordinate-test",
            title = "A report that names Odesa and claims to be in the Pacific",
            content = "Shipping was disrupted at Odesa overnight, according to the port authority.",
            locationName = "Odesa",
            latitude = -40.0,
            longitude = -150.0,
        });

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);

        var observation = await WaitForObservationAsync(client, "coordinate-test");

        // Placed by the gazetteer from the name, not by the caller from the payload.
        var location = observation.GetProperty("location");
        Assert.Equal("Odesa", location.GetProperty("name").GetString());
        Assert.Equal(46.482, location.GetProperty("latitude").GetDouble(), precision: 2);
        Assert.Equal(30.723, location.GetProperty("longitude").GetDouble(), precision: 2);

        // And emphatically not where the caller asked to be put.
        Assert.NotEqual(-40.0, location.GetProperty("latitude").GetDouble(), precision: 2);
        Assert.NotEqual(-150.0, location.GetProperty("longitude").GetDouble(), precision: 2);
    }

    [Fact]
    public async Task ASubmissionNamingNoKnownPlaceStaysUnplaced()
    {
        // The other half of the rule. Refusing the caller's coordinate is only honest if the
        // alternative is an unplaced observation rather than a plausible-looking invented one.
        using var factory = new PipelineFactory(runPipeline: true, runSources: false);
        using var client = factory.CreateClient();

        using var response = await client.PostAsJsonAsync("/api/observations", new
        {
            sourceName = "unplaceable-test",
            title = "A report from somewhere the gazetteer does not know",
            content = "An incident was reported at Nowhere-in-Particular late on Tuesday.",
            locationName = "Nowhere-in-Particular",
            latitude = 12.585,
            longitude = 43.334,
        });

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);

        var observation = await WaitForObservationAsync(client, "unplaceable-test");

        Assert.Equal(JsonValueKind.Null, observation.GetProperty("location").ValueKind);
        Assert.False(string.IsNullOrWhiteSpace(observation.GetProperty("locationResolutionNote").GetString()));
    }

    private static async Task<JsonElement> WaitForObservationAsync(HttpClient client, string source)
    {
        // The endpoint answers 202 before processing, so the read model is polled rather than assumed
        // ready. Bounded so a genuine failure fails the test rather than hanging it.
        for (var attempt = 0; attempt < 50; attempt++)
        {
            var observations = await client.GetFromJsonAsync<JsonElement>("/api/observations?take=50");

            foreach (var observation in observations.EnumerateArray())
            {
                if (observation.GetProperty("sourceName").GetString() == $"manual:{source}"
                    && observation.GetProperty("status").GetString() != "Received")
                {
                    return observation;
                }
            }

            await Task.Delay(100);
        }

        throw new InvalidOperationException($"No processed observation arrived from '{source}'.");
    }
}
