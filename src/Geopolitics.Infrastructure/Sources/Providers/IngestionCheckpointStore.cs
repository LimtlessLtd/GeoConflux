using Geopolitics.Application.Abstractions;
using Geopolitics.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Geopolitics.Infrastructure.Sources.Providers;

/// <summary>
/// Remembers how far back each dataset adapter has asked, across restarts.
/// <para>
/// This is what makes a backfill resumable rather than merely repeatable. A poll spends a bounded
/// number of requests and then stops; without somewhere durable to write down where it stopped, the
/// next poll — and every poll after a deploy, a crash, or a scale-down — would start again from the
/// present and the walk would never reach further back than one poll's budget.
/// </para>
/// </summary>
internal interface IIngestionCheckpointStore
{
    /// <summary>The earliest instant this source has already requested, or null if it has never run.</summary>
    Task<DateTimeOffset?> ReadAsync(string source, CancellationToken cancellationToken);

    /// <summary>Records that history has now been requested from <paramref name="requestedFrom"/> forward.</summary>
    Task WriteAsync(string source, DateTimeOffset requestedFrom, CancellationToken cancellationToken);
}

/// <summary>
/// The checkpoint store over the application's database.
/// <para>
/// Creates a scope per call because the adapters that use it are singletons and a
/// <see cref="DbContext"/> is not. Holding one on a singleton adapter is the standard way this goes
/// wrong; it works in testing and corrupts state the first time two polls overlap.
/// </para>
/// </summary>
internal sealed class IngestionCheckpointStore(IServiceScopeFactory scopeFactory, TimeProvider timeProvider)
    : IIngestionCheckpointStore, ISourceLivenessRecorder
{
    /// <summary>
    /// Writes down that this source asked its provider something, which is the only evidence this
    /// host has that it was running at that moment.
    /// <para>
    /// It shares the checkpoint's table and its key because it answers a question of the same shape —
    /// what does this deployment know about this adapter — and because the row already exists for
    /// the two adapters that backfill. It does not share its rule: the frontier only ever moves
    /// earlier, and this only ever moves later.
    /// </para>
    /// </summary>
    public async Task RecordPollAsync(string source, TimeSpan pollInterval, CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var database = scope.ServiceProvider.GetRequiredService<GeopoliticsDbContext>();

        var checkpoint = await database.Checkpoints
            .FirstOrDefaultAsync(value => value.Source == source, cancellationToken);

        if (checkpoint is null)
        {
            // Created with no backfill frontier. A row existing because a source polled must not
            // answer "how far back has history been requested" — leaving it null is what keeps the
            // walk starting from the live window rather than from whenever this row appeared.
            checkpoint = new IngestionCheckpoint { Source = source };
            database.Checkpoints.Add(checkpoint);
        }

        checkpoint.LastPolledAt = timeProvider.GetUtcNow();
        checkpoint.PollEvery = pollInterval;

        await database.SaveChangesAsync(cancellationToken);
    }

    public async Task<DateTimeOffset?> ReadAsync(string source, CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var database = scope.ServiceProvider.GetRequiredService<GeopoliticsDbContext>();

        var checkpoint = await database.Checkpoints
            .AsNoTracking()
            .FirstOrDefaultAsync(value => value.Source == source, cancellationToken);

        return checkpoint?.RequestedFrom;
    }

    public async Task WriteAsync(string source, DateTimeOffset requestedFrom, CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var database = scope.ServiceProvider.GetRequiredService<GeopoliticsDbContext>();

        var checkpoint = await database.Checkpoints
            .FirstOrDefaultAsync(value => value.Source == source, cancellationToken);

        if (checkpoint is null)
        {
            checkpoint = new IngestionCheckpoint { Source = source };
            database.Checkpoints.Add(checkpoint);
        }
        else if (checkpoint.RequestedFrom <= requestedFrom)
        {
            // Only ever moves earlier. A poll that somehow computed a later frontier — a clock
            // correction, a shortened backfill window, two polls landing out of order — must not
            // undo history already asked for, because the walk would then re-request a span it has
            // covered and stall short of where it had already reached.
            return;
        }

        checkpoint.RequestedFrom = requestedFrom;
        checkpoint.UpdatedAt = timeProvider.GetUtcNow();

        await database.SaveChangesAsync(cancellationToken);
    }
}
