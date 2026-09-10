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
        return Observations is null ? Task.CompletedTask : Observations.SaveChangesAsync(cancellationToken);
    }
}
