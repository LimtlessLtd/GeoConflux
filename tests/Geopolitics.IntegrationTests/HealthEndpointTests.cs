using System.Net;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Geopolitics.IntegrationTests;

public sealed class HealthEndpointTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly WebApplicationFactory<Program> factory;

    public HealthEndpointTests(WebApplicationFactory<Program> factory)
    {
        this.factory = factory;
    }

    [Fact]
    public async Task HealthEndpointReturnsHealthyStatus()
    {
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/api/health");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("Healthy", body, StringComparison.Ordinal);
    }

    /// <summary>
    /// A host that has ingested nothing serves an empty collection, rather than failing or filling
    /// the gap.
    /// <para>
    /// This used to assert that the endpoint returned seeded demo incidents, which a first run wrote
    /// into an empty database so the globe had something on it. Those records were fabricated, and
    /// nothing in this system may present fabricated records as reporting, so the seeder is gone and
    /// an empty first run is the correct answer (ADR 040). The assertion is on the shape rather than
    /// on emptiness because the default host reads whatever database file is already on disk.
    /// </para>
    /// </summary>
    [Fact]
    public async Task IncidentEndpointServesAJsonCollectionWithNothingIngested()
    {
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/api/incidents?take=10");
        var body = await response.Content.ReadAsStringAsync();

        Assert.True(response.IsSuccessStatusCode, body);

        using var document = System.Text.Json.JsonDocument.Parse(body);
        Assert.Equal(System.Text.Json.JsonValueKind.Array, document.RootElement.ValueKind);
    }
}
