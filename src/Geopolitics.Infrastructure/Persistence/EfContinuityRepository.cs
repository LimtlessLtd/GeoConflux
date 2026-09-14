using Geopolitics.Application.Abstractions;
using Microsoft.EntityFrameworkCore;

namespace Geopolitics.Infrastructure.Persistence;

/// <summary>
/// Reads when each source last polled, and keeps the periods this host was away.
/// <para>
/// Both halves come out of the checkpoint table and the downtime table, which are the two places in
/// this schema that describe the process rather than the world.
/// </para>
/// </summary>
public sealed class EfContinuityRepository(GeopoliticsDbContext dbContext) : IContinuityRepository
{
    public async Task<IReadOnlyList<SourceLiveness>> ReadLivenessAsync(CancellationToken cancellationToken)
    {
        // A row exists for any source that has ever polled or backfilled; only the first kind has a
        // poll timestamp, and only that kind is evidence the host was running.
        var rows = await dbContext.Checkpoints
            .AsNoTracking()
            .Where(checkpoint => checkpoint.LastPolledAt != null && checkpoint.PollEvery != null)
            .Select(checkpoint => new
            {
                checkpoint.Source,
                LastPolledAt = checkpoint.LastPolledAt!.Value,
                PollEvery = checkpoint.PollEvery!.Value,
            })
            .ToListAsync(cancellationToken);

        return [.. rows.Select(row => new SourceLiveness(row.Source, row.LastPolledAt, row.PollEvery))];
    }

    public async Task AddDowntimeAsync(DowntimeRecord period, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(period);

        dbContext.Downtime.Add(new DowntimePeriod
        {
            Id = period.Id,
            StartedAt = period.StartedAt,
            EndedAt = period.EndedAt,
            DetectedAt = period.DetectedAt,
            LastSource = period.LastSource,
            Cadence = period.Cadence,
        });

        await dbContext.SaveChangesAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<DowntimeRecord>> ListDowntimeAsync(int take, CancellationToken cancellationToken)
    {
        var periods = await dbContext.Downtime
            .AsNoTracking()
            .OrderByDescending(period => period.StartedAt)
            .Take(Math.Clamp(take, 1, 200))
            .ToListAsync(cancellationToken);

        return
        [
            .. periods.Select(period => new DowntimeRecord(
                period.Id,
                period.StartedAt,
                period.EndedAt,
                period.DetectedAt,
                period.LastSource,
                period.Cadence)),
        ];
    }
}
