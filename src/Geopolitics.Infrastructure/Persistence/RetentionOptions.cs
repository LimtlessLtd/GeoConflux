using Geopolitics.Application.Abstractions;

namespace Geopolitics.Infrastructure.Persistence;

/// <summary>
/// How long this host keeps what it is allowed to delete.
/// <para>
/// Off by default, and that is the shape the sprint asked for rather than caution for its own sake:
/// the rule was measure before deciding, and a policy that switched itself on would be a policy
/// chosen before anybody read a number. The holdings panel is what the number is for.
/// </para>
/// </summary>
public sealed class RetentionOptions
{
    public const string SectionName = "Retention";

    /// <summary>Whether anything is deleted at all.</summary>
    public bool Enabled { get; set; }

    /// <summary>How often the policy runs. Nothing happens between runs.</summary>
    public TimeSpan Interval { get; set; } = TimeSpan.FromHours(24);

    /// <summary>
    /// How long a prunable row is kept, measured from when this host received it.
    /// <para>
    /// Ninety days because that is long enough that a duplicate or a failure has stopped being
    /// something anybody will investigate, and short enough to bound the two tables that grow
    /// without ever being read.
    /// </para>
    /// </summary>
    public TimeSpan Keep { get; set; } = TimeSpan.FromDays(90);
}

/// <summary>
/// What retention has done since this host started.
/// <para>
/// Deliberately in memory rather than in a table. A row per run would outlive a restart and would
/// also be a second thing to prune, and the question it answers — is the policy actually running —
/// is about the host that is running now. The report says which host and since when, so the figure
/// cannot be read as a lifetime total.
/// </para>
/// </summary>
public sealed class RetentionLog
{
    private readonly Lock gate = new();

    private long observations;
    private long inferences;
    private long reclaimed;
    private DateTimeOffset? lastRunAt;

    public void Record(PruneResult result, DateTimeOffset at)
    {
        ArgumentNullException.ThrowIfNull(result);

        lock (gate)
        {
            observations += result.Observations;
            inferences += result.Inferences;
            reclaimed += result.ReclaimedBytes;
            lastRunAt = at;
        }
    }

    public (long Observations, long Inferences, long ReclaimedBytes, DateTimeOffset? LastRunAt) Read()
    {
        lock (gate)
        {
            return (observations, inferences, reclaimed, lastRunAt);
        }
    }
}
