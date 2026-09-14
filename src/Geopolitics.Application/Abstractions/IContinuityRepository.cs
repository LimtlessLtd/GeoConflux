namespace Geopolitics.Application.Abstractions;

/// <param name="Source">The adapter's name.</param>
/// <param name="LastPolledAt">When it last asked its provider anything.</param>
/// <param name="PollEvery">
/// How often it asks. Recorded beside the timestamp because it is what turns a gap into a judgement:
/// eleven hours of silence is a stopped host for a fifteen-minute feed and an ordinary morning for a
/// daily dataset.
/// </param>
public sealed record SourceLiveness(string Source, DateTimeOffset LastPolledAt, TimeSpan PollEvery);

/// <summary>
/// A period during which this host was not running.
/// </summary>
/// <param name="Id">Its identifier, so Sprint 18 can record what it recovered against it.</param>
/// <param name="StartedAt">
/// The last moment the host is known to have been alive. The period deliberately starts here rather
/// than one polling interval later: over-covering costs a re-read that deduplication absorbs, and
/// under-covering loses records nothing will ever ask for again.
/// </param>
/// <param name="EndedAt">When it started again.</param>
/// <param name="DetectedAt">When the gap was noticed, which is at startup.</param>
/// <param name="LastSource">Which adapter's last poll set the start.</param>
/// <param name="Cadence">
/// The fastest polling interval among the sources that were running. The resolution of this
/// measurement: a gap shorter than a couple of these cannot be told from ordinary quiet.
/// </param>
public sealed record DowntimeRecord(
    Guid Id,
    DateTimeOffset StartedAt,
    DateTimeOffset EndedAt,
    DateTimeOffset DetectedAt,
    string LastSource,
    TimeSpan Cadence)
{
    public TimeSpan Duration => EndedAt - StartedAt;
}

/// <summary>
/// Reads when each source last polled, and records the gaps between one run of this host and the
/// next.
/// </summary>
public interface IContinuityRepository
{
    /// <summary>Every source that has ever polled on this deployment, with when and how often.</summary>
    Task<IReadOnlyList<SourceLiveness>> ReadLivenessAsync(CancellationToken cancellationToken);

    Task AddDowntimeAsync(DowntimeRecord period, CancellationToken cancellationToken);

    /// <summary>The most recent periods, newest first.</summary>
    Task<IReadOnlyList<DowntimeRecord>> ListDowntimeAsync(int take, CancellationToken cancellationToken);
}

/// <summary>
/// Records that a source asked its provider something, which is the only evidence this host has that
/// it was running at a given moment.
/// <para>
/// Public, and in the Application layer, because the polling base class has to call it. The thing it
/// writes to is an infrastructure table that records a fact about a process rather than about the
/// world — but the fact that a poll <em>happened</em> is a pipeline event, and the pipeline is here.
/// </para>
/// </summary>
public interface ISourceLivenessRecorder
{
    Task RecordPollAsync(string source, TimeSpan pollInterval, CancellationToken cancellationToken);
}
