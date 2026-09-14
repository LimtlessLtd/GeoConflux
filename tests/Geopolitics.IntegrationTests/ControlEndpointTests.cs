using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace Geopolitics.IntegrationTests;

/// <summary>
/// The one endpoint in this API that publishes a conclusion rather than a record.
/// <para>
/// Which is why the empty case is the one worth asserting. A deployment with no dataset credential
/// polls nothing that codes territorial change, so it assesses nothing — and the payload has to say
/// that is a statement about its own reach rather than about the world. An empty control layer that
/// read as peace would be the failure this whole repository is built to avoid.
/// </para>
/// </summary>
public sealed class ControlEndpointTests
{
    [Fact]
    public async Task AssessingNothingIsReportedAsAGapInReachRatherThanAsQuiet()
    {
        using var factory = new PipelineFactory(runPipeline: true, runSources: true);
        using var client = factory.CreateClient();

        var response = await client.GetAsync(new Uri("/api/control", UriKind.Relative), CancellationToken.None);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var report = await response.Content.ReadFromJsonAsync<JsonElement>(CancellationToken.None);

        // The recorded replay stream carries no coded territorial change, so nothing is assessed.
        Assert.Equal(0, report.GetProperty("placesAssessed").GetInt32());
        Assert.Empty(report.GetProperty("places").EnumerateArray());

        var note = report.GetProperty("note").GetString();
        Assert.Contains("not about the world", note, StringComparison.Ordinal);

        // The method travels with the payload, so a consumer reading this over the API rather than
        // through the dashboard is told what it is and what it is not.
        var method = report.GetProperty("method").GetString();
        Assert.Contains("never from reports of fighting", method, StringComparison.Ordinal);
        Assert.Contains("not a front line", method, StringComparison.Ordinal);
    }
}
