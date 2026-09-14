namespace Geopolitics.Infrastructure.Persistence;

/// <summary>
/// Where copies of the database go, how often, and how many are kept.
/// <para>
/// There is no default destination, and that is the decision rather than an omission. A backup
/// written beside the database survives a bad write and not a failed disk, so a default would ship a
/// setting that looks like a backup and is not — and the deployment most likely to keep the default
/// is the one least likely to notice. Naming a destination is therefore the act of turning this on.
/// </para>
/// </summary>
public sealed class BackupOptions
{
    public const string SectionName = "Backup";

    /// <summary>
    /// Where copies are written. Empty means no backup runs at all, which is the shipped state.
    /// </summary>
    public string Directory { get; set; } = string.Empty;

    /// <summary>
    /// How often a copy is taken. Measured from the newest copy already on disk rather than from
    /// process start, so a host that restarts twice a day still backs up once.
    /// </summary>
    public TimeSpan Interval { get; set; } = TimeSpan.FromHours(24);

    /// <summary>
    /// How many copies to keep. Older ones are deleted only after a new one has been verified, so a
    /// run that produces a corrupt copy cannot also be the run that deletes the last good one.
    /// </summary>
    public int Keep { get; set; } = 7;

    public bool IsEnabled => !string.IsNullOrWhiteSpace(Directory);
}
