namespace Geopolitics.Application.Abstractions;

/// <param name="Observations">Duplicate and failed observations removed.</param>
/// <param name="Inferences">Audit rows removed because the observation they describe no longer exists.</param>
/// <param name="ReclaimedBytes">
/// What a vacuum returned to the filesystem, or nought if none ran. Deleting rows alone moves space
/// onto SQLite's free list and gives nothing back.
/// </param>
public sealed record PruneResult(long Observations, long Inferences, long ReclaimedBytes)
{
    public long Rows => Observations + Inferences;

    public static PruneResult None { get; } = new(0, 0, 0);
}

/// <summary>
/// Deletes what a host is allowed to delete, and returns the space.
/// <para>
/// Separate from <see cref="IOperationsRepository"/>, which only measures. Keeping the thing that
/// reads and the thing that destroys in different interfaces is not ceremony here: the report is
/// served to anybody who can reach the dashboard, and there is no request that should be able to
/// reach this.
/// </para>
/// </summary>
public interface IRetentionRepository
{
    /// <summary>How many rows the policy would delete right now, without deleting any of them.</summary>
    Task<long> CountPrunableAsync(DateTimeOffset horizon, CancellationToken cancellationToken);

    /// <summary>Deletes what the policy allows older than <paramref name="horizon"/>.</summary>
    Task<PruneResult> PruneAsync(DateTimeOffset horizon, CancellationToken cancellationToken);
}
