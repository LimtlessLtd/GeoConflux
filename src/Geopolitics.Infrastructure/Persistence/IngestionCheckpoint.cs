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
    /// </summary>
    public DateTimeOffset RequestedFrom { get; set; }

    /// <summary>When that last moved, so an operator can see whether a backfill is progressing.</summary>
    public DateTimeOffset UpdatedAt { get; set; }
}
