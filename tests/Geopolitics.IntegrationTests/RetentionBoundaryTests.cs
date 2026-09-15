using Geopolitics.Application.Abstractions;
using Geopolitics.Domain;
using Geopolitics.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Geopolitics.IntegrationTests;

/// <summary>
/// The line retention is not allowed to cross, asserted against a real database rather than argued.
/// <para>
/// [ADR 023] draws it from the failure side: a failed commit keeps the evidence and drops the
/// derived state. Retention approaches the same line from the other side, and crossing it produces
/// the one artefact this repository refuses to publish — an incident asserting something with
/// nothing behind it. The guarantee is worth a test against a real store because it depends on a
/// real invariant, and a fake that was taught the rule could not fail to keep it.
/// </para>
/// </summary>
public sealed class RetentionBoundaryTests
{
    private static readonly TimeSpan Horizon = TimeSpan.FromDays(90);

    [Fact]
    public async Task PruningNeverRemovesEvidenceAnIncidentRestsOn()
    {
        using var factory = new PipelineFactory(runPipeline: true, runSources: true, scriptedStream: true);
        using var client = factory.CreateClient();

        await WaitForTheScriptedRunToFinishAsync(factory);

        using var scope = factory.Services.CreateScope();
        var database = scope.ServiceProvider.GetRequiredService<GeopoliticsDbContext>();

        // Everything already stored is aged past the horizon, so nothing survives the prune merely
        // by being recent. What survives, survives because the policy protects it.
        var aged = DateTimeOffset.UtcNow - Horizon - TimeSpan.FromDays(10);
        await database.Observations.ExecuteUpdateAsync(
            update => update.SetProperty(observation => observation.ReceivedAt, aged),
            CancellationToken.None);

        database.Observations.Add(Aged(ObservationStatus.Duplicate, aged, "a repeat delivery"));
        database.Observations.Add(Aged(ObservationStatus.Failed, aged, "a report that could not be processed"));
        await database.SaveChangesAsync(CancellationToken.None);

        var linkedBefore = await database.Observations.CountAsync(o => o.IncidentId != null, CancellationToken.None);
        var incidents = await database.Incidents.AsNoTracking().ToListAsync(CancellationToken.None);

        Assert.NotEmpty(incidents);
        Assert.True(linkedBefore > 0, "the scripted run should have produced observations linked to incidents");

        var retention = scope.ServiceProvider.GetRequiredService<IRetentionRepository>();
        var result = await retention.PruneAsync(DateTimeOffset.UtcNow - Horizon, CancellationToken.None);

        // It did do something — otherwise the assertions below would pass against a no-op.
        Assert.True(result.Observations >= 2, $"expected the duplicate and the failure to go; removed {result.Observations}");

        // Every incident's evidence still resolves. This is the assertion the whole file exists for.
        var surviving = await database.Observations.AsNoTracking()
            .Select(observation => observation.Id)
            .ToListAsync(CancellationToken.None);

        foreach (var incident in incidents)
        {
            foreach (var evidence in incident.ObservationIds)
            {
                Assert.Contains(evidence, surviving);
            }
        }

        Assert.Equal(
            linkedBefore,
            await database.Observations.CountAsync(o => o.IncidentId != null, CancellationToken.None));

        Assert.Equal(0, await database.Observations.CountAsync(
            o => o.Status == ObservationStatus.Duplicate || o.Status == ObservationStatus.Failed,
            CancellationToken.None));
    }

    [Fact]
    public async Task AClaimHeldForCorroborationSurvivesTheHorizon()
    {
        // Unlinked, and deliberately not prunable. A held claim is drawn on the map and counted in
        // the coverage panel's split of published reporting against user-generated claims, so
        // deleting it would change what the page says about its own composition — silently.
        using var factory = new PipelineFactory(runPipeline: false, runSources: false);
        using var client = factory.CreateClient();

        using var scope = factory.Services.CreateScope();
        var database = scope.ServiceProvider.GetRequiredService<GeopoliticsDbContext>();

        var aged = DateTimeOffset.UtcNow - Horizon - TimeSpan.FromDays(10);
        var held = Aged(
            ObservationStatus.Received,
            aged,
            "a single post nothing else supports",
            SourceAttribution.Post("telegram", "@example"));

        held.HoldAsUncorroborated();

        database.Observations.Add(held);
        await database.SaveChangesAsync(CancellationToken.None);

        var retention = scope.ServiceProvider.GetRequiredService<IRetentionRepository>();
        await retention.PruneAsync(DateTimeOffset.UtcNow - Horizon, CancellationToken.None);

        Assert.True(await database.Observations.AnyAsync(o => o.Id == held.Id, CancellationToken.None));
    }

    private static RawObservation Aged(
        ObservationStatus status,
        DateTimeOffset receivedAt,
        string content,
        SourceAttribution? attribution = null)
    {
        var observation = new RawObservation(
            Guid.NewGuid(),
            ObservationKind.ExternalEvent,
            "retention-tests",
            $"{content}, {Guid.NewGuid():N}",
            $"retention-{Guid.NewGuid():N}",
            receivedAt,
            ObservationProvenance.Polled,
            attribution: attribution);

        switch (status)
        {
            case ObservationStatus.Duplicate:
                observation.MarkDuplicate(Guid.NewGuid());
                break;
            case ObservationStatus.Failed:
                observation.MarkFailed("kept so somebody can work out why, and not for ever");
                break;
            default:
                break;
        }

        return observation;
    }

    /// <summary>
    /// Waits for the whole scripted run to be stored, which is determinate because the fixture is
    /// finite and emits once: when every record it holds has been stored there is nothing left for
    /// the pump to add.
    /// <para>
    /// This used to wait for the first incident to appear and called that determinate. It is not.
    /// The condition is satisfied while the rest of the stream is still being ingested, so the
    /// counts the caller then takes are a snapshot of a run in progress — and the assertion that
    /// pruning leaves the linked count unchanged failed whenever one more observation happened to
    /// be linked between the two reads. Observed failing two runs in three. The comment asserting
    /// the race was gone outlived the race being gone, which is the more useful half of the lesson.
    /// </para>
    /// </summary>
    private static async Task WaitForTheScriptedRunToFinishAsync(PipelineFactory factory)
    {
        var deadline = DateTimeOffset.UtcNow.AddMinutes(2);
        var stored = 0;

        while (DateTimeOffset.UtcNow < deadline)
        {
            using var scope = factory.Services.CreateScope();
            var database = scope.ServiceProvider.GetRequiredService<GeopoliticsDbContext>();

            stored = await database.Observations.AsNoTracking().CountAsync(CancellationToken.None);

            if (stored >= ScriptedEventSource.RecordCount
                && await database.Observations.AsNoTracking().AnyAsync(o => o.IncidentId != null, CancellationToken.None))
            {
                return;
            }

            await Task.Delay(100, CancellationToken.None);
        }

        Assert.Fail(
            $"the scripted run stored {stored} of {ScriptedEventSource.RecordCount} observations within two minutes, "
            + "so there is no settled state to assert against");
    }
}
