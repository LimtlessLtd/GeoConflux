using Geopolitics.Application.Abstractions;
using Microsoft.Extensions.Logging;

namespace Geopolitics.Application.Operations;

/// <summary>Works out whether this host has been away, and writes it down if it has.</summary>
public interface IContinuityService
{
    /// <summary>
    /// Compares the newest poll on record against now, and records a downtime period if the gap is
    /// longer than ordinary quiet. Returns null when there is nothing to record.
    /// </summary>
    Task<DowntimeRecord?> RecordStartupGapAsync(CancellationToken cancellationToken);

    Task<IReadOnlyList<DowntimeRecord>> RecentAsync(int take, CancellationToken cancellationToken);

    Task<IReadOnlyList<SourceLiveness>> LivenessAsync(CancellationToken cancellationToken);
}

/// <summary>
/// The downtime ledger: what this host missed by not running.
/// <para>
/// A machine that is switched off misses the world, and the world does not tell it so on the way
/// back. The only evidence a host has of having been alive at a given moment is that one of its
/// adapters asked a provider something then — so the newest such timestamp is the last moment it is
/// known to have been up, and the distance from there to startup is the gap.
/// </para>
/// <para>
/// Sprint 18 is what consumes this. A dataset is an archive and still holds what happened while the
/// machine was off, so a recorded interval is a window to go and ask for. A social feed is a rolling
/// window and does not, so the same interval is a statement about what cannot be recovered. Both
/// need the interval to exist first, written down at the moment it can still be worked out.
/// </para>
/// </summary>
public sealed partial class ContinuityService(
    IContinuityRepository repository,
    TimeProvider timeProvider,
    ILogger<ContinuityService> logger) : IContinuityService
{
    /// <summary>
    /// A gap has to exceed this many polling intervals before it counts.
    /// <para>
    /// Two, because one is ambiguous. A poll that ran slightly late, a provider that took a minute,
    /// or a clock that moved by a second all produce a gap of just over one interval, and recording
    /// those as downtime would fill the ledger with noise that Sprint 18 would then go and re-request
    /// history for. Two intervals means a source demonstrably missed a poll.
    /// </para>
    /// </summary>
    private const int MissedPolls = 2;

    /// <summary>
    /// And a floor beneath that, so a fast feed does not report every deployment as an outage. A
    /// host restarted in thirty seconds was not down in any sense a reader cares about.
    /// </summary>
    private static readonly TimeSpan ShortestWorthRecording = TimeSpan.FromMinutes(15);

    public async Task<DowntimeRecord?> RecordStartupGapAsync(CancellationToken cancellationToken)
    {
        var liveness = await repository.ReadLivenessAsync(cancellationToken);

        if (liveness.Count == 0)
        {
            // Either this deployment is new, or nothing here polls. Both mean the same thing for
            // this purpose: there is no evidence of a previous run, so there is no gap to measure.
            // It is not "no downtime" and must not be recorded as though it were.
            LogNoEvidence(logger);
            return null;
        }

        // The newest poll across all sources, because any one of them polling proves the host was
        // up. The fastest cadence among them is the resolution of the measurement: if the host had
        // been running, that source would have polled within one of those.
        var newest = liveness.MaxBy(source => source.LastPolledAt)!;
        var cadence = liveness.Min(source => source.PollEvery);

        var now = timeProvider.GetUtcNow();
        var gap = now - newest.LastPolledAt;

        var threshold = Max(cadence * MissedPolls, ShortestWorthRecording);

        if (gap <= threshold)
        {
            LogContinuous(logger, gap);
            return null;
        }

        var period = new DowntimeRecord(Guid.NewGuid(), newest.LastPolledAt, now, now, newest.Source, cadence);
        await repository.AddDowntimeAsync(period, cancellationToken);

        LogRecorded(logger, period.StartedAt, period.EndedAt, period.Duration, newest.Source);
        return period;
    }

    public Task<IReadOnlyList<DowntimeRecord>> RecentAsync(int take, CancellationToken cancellationToken) =>
        repository.ListDowntimeAsync(take, cancellationToken);

    public Task<IReadOnlyList<SourceLiveness>> LivenessAsync(CancellationToken cancellationToken) =>
        repository.ReadLivenessAsync(cancellationToken);

    private static TimeSpan Max(TimeSpan left, TimeSpan right) => left > right ? left : right;

    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "No source has ever polled on this deployment, so there is no record of a previous run and no gap can be measured.")]
    private static partial void LogNoEvidence(ILogger logger);

    [LoggerMessage(Level = LogLevel.Information, Message = "This host was last polling {Gap} ago, which is ordinary quiet rather than downtime.")]
    private static partial void LogContinuous(ILogger logger, TimeSpan gap);

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "This host was not running between {StartedAt} and {EndedAt}, a gap of {Duration}, measured from the last poll by {LastSource}.")]
    private static partial void LogRecorded(
        ILogger logger,
        DateTimeOffset startedAt,
        DateTimeOffset endedAt,
        TimeSpan duration,
        string lastSource);
}
