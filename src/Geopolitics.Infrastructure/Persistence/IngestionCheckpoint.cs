namespace Geopolitics.Infrastructure.Persistence;

/// <summary>
/// How far back through a dataset's history one adapter has already asked.
/// <para>
/// Deliberately not in the domain project. This records nothing about the world; it records what
/// this deployment has requested, which is a fact about a process rather than about a conflict. The
/// domain has no use for it and should not carry it.
/// </para>
/// <para>
/// It is also deliberately not derived from the stored observations, which is the obvious
/// alternative — the oldest record from a source looks like it would say the same thing for free.
/// It would not. "The earliest event we hold" and "the earliest date we have asked about" are
/// different facts, and they come apart exactly where it matters: a quiet fortnight in a small
/// country returns no events at all, the oldest stored record does not move, and a backfill driven
/// from it would request that same empty fortnight on every poll for the rest of the deployment's
/// life without ever reaching the month before it.
/// </para>
/// </summary>
public sealed class IngestionCheckpoint
{
    /// <summary>The adapter's name, matching <see cref="Application.Abstractions.IEventSource.Name"/>.</summary>
    public string Source { get; set; } = string.Empty;

    /// <summary>
    /// The earliest instant this adapter has requested. History is considered asked-for from here
    /// forward; the backfill walks this value earlier one window at a time.
    /// <para>
    /// Null when this adapter has never backfilled, which is the shipped default and is a different
    /// fact from having backfilled as far as the present. The backfill reads a null as "start from
    /// the live window"; a row created by an ordinary poll must not answer that question at all.
    /// </para>
    /// </summary>
    public DateTimeOffset? RequestedFrom { get; set; }

    /// <summary>When that last moved, so an operator can see whether a backfill is progressing.</summary>
    public DateTimeOffset? UpdatedAt { get; set; }

    /// <summary>
    /// When this adapter last asked its provider anything, successfully or not.
    /// <para>
    /// This is the downtime ledger's only evidence. A host has no other way to know it was running
    /// at a given moment: the observations it stored carry the times sources reported, not the times
    /// this process was alive, and a backfill routinely stores records dated years ago. A poll
    /// happening is the one thing that can only be true of a running host.
    /// </para>
    /// <para>
    /// Written on every poll including a failed one. A provider being unreachable says nothing about
    /// whether this machine was switched on, and treating a failed poll as silence would record an
    /// outage at the provider as an outage here.
    /// </para>
    /// </summary>
    public DateTimeOffset? LastPolledAt { get; set; }

    /// <summary>
    /// How often this adapter polls, as of that last poll.
    /// <para>
    /// Stored rather than read from configuration because it has to describe the run that produced
    /// the timestamp beside it. A deployment that changed an interval from fifteen minutes to a day
    /// would otherwise have the ledger judge yesterday's gap against today's setting.
    /// </para>
    /// </summary>
    public TimeSpan? PollEvery { get; set; }
}

/// <summary>
/// A period during which this host was not running.
/// <para>
/// In the infrastructure project for the same reason <see cref="IngestionCheckpoint"/> is: it records
/// nothing about the world. It is a fact about a process, and the domain has no use for it.
/// </para>
/// <para>
/// A row rather than a log line because Sprint 18 has to act on it. Recovering a fortnight's downtime
/// means pointing the existing dataset window walk at the interval, and stating what a rolling social
/// feed lost over the same one — neither of which can be done from a message that has scrolled away.
/// </para>
/// </summary>
public sealed class DowntimePeriod
{
    public Guid Id { get; set; }

    /// <summary>The last moment the host is known to have been alive.</summary>
    public DateTimeOffset StartedAt { get; set; }

    /// <summary>When it started again.</summary>
    public DateTimeOffset EndedAt { get; set; }

    /// <summary>When the gap was noticed, which is at startup.</summary>
    public DateTimeOffset DetectedAt { get; set; }

    /// <summary>Which adapter's last poll set the start.</summary>
    public string LastSource { get; set; } = string.Empty;

    /// <summary>
    /// The fastest polling interval among the sources that were running, which is the resolution of
    /// the measurement. Recorded so a reader is not left to assume the period is exact.
    /// </summary>
    public TimeSpan Cadence { get; set; }
}
