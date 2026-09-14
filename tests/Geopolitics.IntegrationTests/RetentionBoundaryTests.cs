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
        using var factory = new PipelineFactory(runPipeline: true, runSources: true);
        using var client = factory.CreateClient();

        await WaitForIncidentsAsync(factory);

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
        Assert.True(linkedBefore > 0, "the replay run should have produced observations linked to incidents");

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
    /// Waits for a determinate condition — incidents existing — rather than for the pipeline going
    /// quiet, which is a heuristic that races.
    /// </summary>
    private static async Task WaitForIncidentsAsync(PipelineFactory factory)
    {
        var deadline = DateTimeOffset.UtcNow.AddMinutes(2);

        while (DateTimeOffset.UtcNow < deadline)
        {
            using var scope = factory.Services.CreateScope();
            var database = scope.ServiceProvider.GetRequiredService<GeopoliticsDbContext>();

            if (await database.Incidents.AsNoTracking().AnyAsync(CancellationToken.None)
                && await database.Observations.AsNoTracking().AnyAsync(o => o.IncidentId != null, CancellationToken.None))
            {
                return;
            }

            await Task.Delay(100, CancellationToken.None);
        }

        Assert.Fail("the replay run produced no incident within two minutes, so there is no evidence to protect");
    }
}
