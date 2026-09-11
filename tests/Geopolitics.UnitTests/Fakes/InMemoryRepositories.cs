using Geopolitics.Application.Abstractions;
using Geopolitics.Application.Contracts;
using Geopolitics.Domain;

namespace Geopolitics.UnitTests.Fakes;

/// <summary>
/// Hand-written in-memory repositories. They are deliberately literal about the behaviour the real
/// EF implementations provide — in particular that <c>AddAsync</c> stages work and <c>SaveChangesAsync</c>
/// commits it — so that a test cannot pass against a fake that is more forgiving than the database.
/// </summary>
public sealed class FakeObservationRepository : IObservationRepository
{
    private readonly List<RawObservation> committed = [];
    private readonly List<RawObservation> pending = [];

    public IReadOnlyList<RawObservation> Committed => committed;

    public int SaveCount { get; private set; }

    /// <summary>Set to make the next save throw, for exercising failure paths.</summary>
    public Exception? SaveException { get; set; }

    public Task<Guid?> FindByFingerprintAsync(string fingerprint, CancellationToken cancellationToken) =>
        Task.FromResult(committed
            .Where(value => value.Fingerprint == fingerprint && value.Status != ObservationStatus.Duplicate)
            .Select(value => (Guid?)value.Id)
            .FirstOrDefault());

    public Task AddAsync(RawObservation observation, CancellationToken cancellationToken)
    {
        pending.Add(observation);
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<RawObservation>> ListRecentAsync(int take, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<RawObservation>>(
            committed.OrderByDescending(value => value.ReceivedAt).Take(take).ToArray());

    public Task<IReadOnlyList<RawObservation>> ListByIncidentAsync(Guid incidentId, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<RawObservation>>(
            committed.Where(value => value.IncidentId == incidentId).ToArray());

    public Task SaveChangesAsync(CancellationToken cancellationToken)
    {
        if (SaveException is { } exception)
        {
            SaveException = null;
            throw exception;
        }

        SaveCount++;
        committed.AddRange(pending);
        pending.Clear();
        return Task.CompletedTask;
    }
}

public sealed class FakeIncidentRepository : IIncidentRepository
{
    private readonly List<GeopoliticalIncident> committed = [];
    private readonly List<GeopoliticalIncident> pending = [];

    public IReadOnlyList<GeopoliticalIncident> Committed => committed;

    /// <summary>Shares a unit of work with the observation repository, as the EF versions do.</summary>
    public FakeObservationRepository? Observations { get; set; }

    /// <summary>Also part of that unit of work: one EF context backs all three in production.</summary>
    public FakeAiInferenceRepository? Inferences { get; set; }

    public void Seed(GeopoliticalIncident incident) => committed.Add(incident);

    public Task<GeopoliticalIncident?> GetByIdAsync(Guid id, CancellationToken cancellationToken) =>
        Task.FromResult(committed.SingleOrDefault(value => value.Id == id));

    public Task<IReadOnlyList<GeopoliticalIncident>> ListAsync(IncidentSearch search, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<GeopoliticalIncident>>(
            committed.OrderByDescending(value => value.OccurredAt).Take(search.BoundedTake).ToArray());

    public Task<IReadOnlyList<GeopoliticalIncident>> ListCorrelationCandidatesAsync(
        EventType eventType,
        DateTimeOffset windowStart,
        DateTimeOffset windowEnd,
        CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<GeopoliticalIncident>>(committed
            .Where(value => value.EventType == eventType
                && value.OccurredAt >= windowStart
                && value.OccurredAt <= windowEnd)
            .ToArray());

    public Task AddAsync(GeopoliticalIncident incident, CancellationToken cancellationToken)
    {
        pending.Add(incident);
        return Task.CompletedTask;
    }

    public Task SaveChangesAsync(CancellationToken cancellationToken)
    {
        committed.AddRange(pending);
        pending.Clear();
        Inferences?.Commit();
        return Observations is null ? Task.CompletedTask : Observations.SaveChangesAsync(cancellationToken);
    }
}

/// <summary>
/// In-memory audit store. Like the EF implementation it stages on add and commits on save, so a
/// test asserting that an inference was recorded is asserting that it would have been committed
/// alongside the observation rather than merely handed to a repository.
/// </summary>
public sealed class FakeAiInferenceRepository : IAiInferenceRepository
{
    private readonly List<AiInference> committed = [];
    private readonly List<AiInference> pending = [];

    public IReadOnlyList<AiInference> Committed => committed;

    /// <summary>Set to make the next add throw, for exercising audit-failure tolerance.</summary>
    public Exception? AddException { get; set; }

    public Task AddAsync(AiInference inference, CancellationToken cancellationToken)
    {
        if (AddException is { } exception)
        {
            AddException = null;
            throw exception;
        }

        pending.Add(inference);
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<AiInference>> ListByObservationAsync(Guid observationId, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<AiInference>>(
            committed.Where(value => value.ObservationId == observationId).ToArray());

    public Task SaveChangesAsync(CancellationToken cancellationToken)
    {
        Commit();
        return Task.CompletedTask;
    }

    /// <summary>Called by the repositories that share this unit of work.</summary>
    public void Commit()
    {
        committed.AddRange(pending);
        pending.Clear();
    }
}
