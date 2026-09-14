using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace Geopolitics.IntegrationTests;

/// <summary>
/// The operations report, measured against a real SQLite file rather than a stub.
/// <para>
/// Most of it could be asserted in memory, and the part that matters could not: page counts, free
/// pages and journal mode come from the storage engine, and an in-memory provider would answer them
/// differently or not at all. The figure this endpoint exists to publish would then be verified
/// against something that is not the database.
/// </para>
/// </summary>
public sealed class OperationsEndpointTests
{
    [Fact]
    public async Task TheHostReportsWhatItHoldsRatherThanEstimatingIt()
    {
        using var factory = new PipelineFactory(runPipeline: true, runSources: true);
        using var client = factory.CreateClient();

        var response = await client.GetAsync(new Uri("/api/operations", UriKind.Relative), CancellationToken.None);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var report = await response.Content.ReadFromJsonAsync<JsonElement>(CancellationToken.None);
        var holdings = report.GetProperty("holdings");

        var tables = holdings.GetProperty("tables")
            .EnumerateArray()
            .ToDictionary(
                table => table.GetProperty("table").GetString()!,
                table => table.GetProperty("rows").GetInt64(),
                StringComparer.Ordinal);

        // Every mapped table is present, taken from the EF model rather than from a list somebody
        // maintains by hand. A table added by a later migration appears here the day it exists.
        Assert.Contains("observations", tables);
        Assert.Contains("incidents", tables);
        Assert.Contains("ai_inferences", tables);
        Assert.Contains("IngestionCheckpoints", tables);

        // A real file has pages, so its size is positive whether or not anything has been ingested.
        Assert.True(holdings.GetProperty("storage").GetProperty("databaseBytes").GetInt64() > 0);

        // No path, host name or connection string. The decision this report supports is about size,
        // and an unauthenticated endpoint naming a file on the operator's disk buys nothing for it.
        var body = report.GetRawText();
        Assert.DoesNotContain(".db", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Data Source", body, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task AFreshDatabaseDeclinesToProjectAYearFromItsFirstMinute()
    {
        // The published page runs against a database created for the build. Reporting a year of
        // growth from it would be a figure with a year's authority and a minute's support.
        using var factory = new PipelineFactory(runPipeline: false, runSources: false);
        using var client = factory.CreateClient();

        var report = await client.GetFromJsonAsync<JsonElement>(
            new Uri("/api/operations", UriKind.Relative), CancellationToken.None);

        var growth = report.GetProperty("holdings").GetProperty("growth");

        Assert.Equal(JsonValueKind.Null, growth.GetProperty("projectedYearBytes").ValueKind);
        Assert.Contains("less than seven days", growth.GetProperty("basis").GetString(), StringComparison.Ordinal);
    }
}
