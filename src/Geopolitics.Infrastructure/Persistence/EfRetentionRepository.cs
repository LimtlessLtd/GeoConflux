using Geopolitics.Application.Abstractions;
using Geopolitics.Application.Operations;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Geopolitics.Infrastructure.Persistence;

/// <summary>
/// The retention policy as a query, and the vacuum that makes it show up on disk.
/// <para>
/// Two guards, and the second one is not redundant. The status filter is the policy; the
/// <c>IncidentId is null</c> filter is the invariant underneath it. They should never disagree —
/// <c>MarkDuplicate</c> clears the link and a failed commit rolls its incident back — and if they
/// ever did, the cost of trusting the first alone is an incident left asserting something with no
/// evidence behind it. Two cheap predicates against that is not a trade worth thinking about.
/// </para>
/// </summary>
public sealed class EfRetentionRepository(GeopoliticsDbContext dbContext) : IRetentionRepository
{
    /// <summary>
    /// Below this share of the database, a vacuum is not worth its cost. It rewrites the whole file
    /// and holds a write lock for the length of it, which on a host that is meant to stay serving is
    /// a real interruption to buy back a rounding error.
    /// </summary>
    private const double VacuumThreshold = 0.10;

    public Task<long> CountPrunableAsync(DateTimeOffset horizon, CancellationToken cancellationToken) =>
        Prunable(horizon).LongCountAsync(cancellationToken);

    public async Task<PruneResult> PruneAsync(DateTimeOffset horizon, CancellationToken cancellationToken)
    {
        var observations = await Prunable(horizon).ExecuteDeleteAsync(cancellationToken);

        // Audit rows whose observation no longer exists. ADR 023 gives these no foreign key on
        // purpose, so that the record of having called a model survives an observation that could
        // not be stored — but that is about a failed save, not about forever. Once the observation
        // is gone and the horizon has passed, the row describes nothing that exists.
        var inferences = await dbContext.Inferences
            .Where(inference => inference.CreatedAt < horizon)
            .Where(inference => !dbContext.Observations.Any(observation => observation.Id == inference.ObservationId))
            .ExecuteDeleteAsync(cancellationToken);

        var reclaimed = observations + inferences > 0
            ? await VacuumAsync(cancellationToken)
            : 0;

        return new PruneResult(observations, inferences, reclaimed);
    }

    /// <summary>
    /// The observations the policy permits deleting, older than the horizon.
    /// </summary>
    /// <remarks>
    /// Aged on <c>ReceivedAt</c> rather than <c>OccurredAt</c>. A source is free to report an event
    /// dated years ago — a dataset backfill does exactly that, by design — and ageing on the event
    /// date would have a backfill delete its own output as it arrived. What retention is about is
    /// how long this host has been carrying a row, which is when it received it.
    /// </remarks>
    private IQueryable<Domain.RawObservation> Prunable(DateTimeOffset horizon) =>
        dbContext.Observations
            .Where(observation => observation.ReceivedAt < horizon)
            .Where(observation => RetentionPolicy.Prunable.Contains(observation.Status))
            .Where(observation => observation.IncidentId == null);

    /// <summary>
    /// Returns freed pages to the filesystem, if there are enough of them to be worth the rewrite.
    /// </summary>
    /// <remarks>
    /// Deleting rows in SQLite moves their pages onto a free list for reuse and returns nothing to
    /// the disk. Without this the holdings panel would show a database that never shrinks however
    /// much was pruned, and the obvious conclusion — that retention is not working — would be wrong
    /// and unfalsifiable.
    /// </remarks>
    private async Task<long> VacuumAsync(CancellationToken cancellationToken)
    {
        await dbContext.Database.OpenConnectionAsync(cancellationToken);

        try
        {
            var connection = (SqliteConnection)dbContext.Database.GetDbConnection();

            var pageSize = await ScalarAsync(connection, "PRAGMA page_size;", cancellationToken);
            var pages = await ScalarAsync(connection, "PRAGMA page_count;", cancellationToken);
            var free = await ScalarAsync(connection, "PRAGMA freelist_count;", cancellationToken);

            if (pages == 0 || (double)free / pages < VacuumThreshold)
            {
                return 0;
            }

            var before = pageSize * pages;
            await ExecuteAsync(connection, "VACUUM;", cancellationToken);
            var after = pageSize * await ScalarAsync(connection, "PRAGMA page_count;", cancellationToken);

            return Math.Max(0, before - after);
        }
        finally
        {
            await dbContext.Database.CloseConnectionAsync();
        }
    }

    private static async Task<long> ScalarAsync(SqliteConnection connection, string sql, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        var value = await command.ExecuteScalarAsync(cancellationToken);
        return value is null or DBNull ? 0 : Convert.ToInt64(value, System.Globalization.CultureInfo.InvariantCulture);
    }

    private static async Task ExecuteAsync(SqliteConnection connection, string sql, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
