using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Geopolitics.Application.Abstractions;
using Geopolitics.Application.Contracts;
using Geopolitics.Domain;
using Microsoft.Extensions.DependencyInjection;

namespace Geopolitics.IntegrationTests;

/// <summary>
/// Conflict membership through the real host, against the real register.
/// <para>
/// The unit tests assert the predicate against fixtures they wrote. This asserts the two things
/// fixtures cannot: that the 319-conflict extract loads and resolves inside a running host, and that
/// a membership survives the database. The second is the one worth a test of its own — the keys live
/// in a JSON column, and an unmapped collection comes back empty after a reload, so a report that
/// belonged to a war would quietly stop belonging to it the moment it left memory.
/// </para>
/// </summary>
public sealed class ConflictMembershipTests
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };

    [Fact]
    public void TheRegisterLoadsInsideTheHostAndHoldsAcodingProjectsConflictsRatherThanThisOnes()
    {
        using var factory = new PipelineFactory(runPipeline: false, runSources: false);
        using var scope = factory.Services.CreateScope();

        var register = scope.ServiceProvider.GetRequiredService<IConflictRegister>();

        Assert.True(
            register.All.Count > 300,
            $"The host started with {register.All.Count} conflicts registered, which is not the extract.");

        Assert.Contains("Uppsala", register.Provenance, StringComparison.Ordinal);
        Assert.All(register.All, conflict => Assert.Equal(ConflictOrigin.Coded, conflict.Origin));
    }

    [Fact]
    public async Task AreportsMembershipSurvivesTheDatabase()
    {
        using var factory = new PipelineFactory(runPipeline: true, runSources: false);
        using var client = factory.CreateClient();

        // A place the register holds and only one conflict has coded events at, submitted with no
        // category, no actors and no coordinates. Everything the stored membership carries has to
        // come from resolving that name and asking the register about it.
        var response = await client.PostAsJsonAsync(
            new Uri("/api/observations", UriKind.Relative),
            new
            {
                sourceName = "analyst-desk",
                content = "Shelling was reported overnight around Pokrovsk with no casualties confirmed.",
                locationName = "Pokrovsk",
            },
            Json);

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);

        var observation = Assert.Single(await WaitForObservationsAsync(client, expected: 1));

        Assert.Contains("ucdp:13243", observation.Conflicts.Keys);
        Assert.NotNull(observation.Conflicts.Basis);

        // Read back over HTTP, which means it was written to SQLite, read out again, and projected —
        // not held in the object the processor happened to leave behind.
        Assert.NotEqual(ConflictMatchBasis.None, observation.Conflicts.Basis);
    }

    [Fact]
    public async Task AreportNoConflictReachesIsStoredWithTheReasonRatherThanWithSilence()
    {
        using var factory = new PipelineFactory(runPipeline: true, runSources: false);
        using var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync(
            new Uri("/api/observations", UriKind.Relative),
            new
            {
                sourceName = "analyst-desk",
                content = "A ferry service resumed between the islands after maintenance.",
                locationName = "Reykjavik",
            },
            Json);

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);

        var observation = Assert.Single(await WaitForObservationsAsync(client, expected: 1));

        Assert.Empty(observation.Conflicts.Keys);
        Assert.Null(observation.Conflicts.Basis);

        // The distinction the note exists for. "No conflict covers this" and "nothing was assigned"
        // look identical in an empty list, and only one of them is a statement about the world.
        Assert.False(string.IsNullOrWhiteSpace(observation.Conflicts.Note));
    }

    private static async Task<List<ObservationResponse>> WaitForObservationsAsync(HttpClient client, int expected)
    {
        var deadline = DateTime.UtcNow.AddSeconds(30);
        List<ObservationResponse> observations = [];

        while (DateTime.UtcNow < deadline)
        {
            observations = await client.GetFromJsonAsync<List<ObservationResponse>>("/api/observations?take=100", Json)
                ?? [];

            if (observations.Count >= expected)
            {
                return observations;
            }

            await Task.Delay(100);
        }

        Assert.Fail($"Expected at least {expected} observations within the timeout but saw {observations.Count}.");
        return observations;
    }
}
