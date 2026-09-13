namespace Geopolitics.Infrastructure.Sources.Briefs;

/// <summary>
/// Configuration for the collected-bundle source.
/// <para>
/// Deliberately not gated by <c>Providers:Mode</c>. That switch exists to guarantee that a clone of
/// this repository makes no external call and needs no credential, and reading a committed JSON file
/// does neither — so gating it there would express nothing while blurring what the switch means.
/// </para>
/// <para>
/// Enabled by default for the same reason it is safe to be: a fresh clone then shows real, cited,
/// dated reporting with no configuration. Because that data is real, such a run is not demo data,
/// and the snapshot labels it as collected rather than as either of the other two things.
/// </para>
/// </summary>
public sealed class AgentBriefOptions
{
    public const string SectionName = "Providers:AgentBriefs";

    public bool Enabled { get; set; } = true;

    /// <summary>Directory holding bundle files, relative to the content root unless rooted.</summary>
    public string Directory { get; set; } = "data/osint";

    /// <summary>
    /// How old a bundle may be before it is skipped. A bundle is a point-in-time capture, and a page
    /// that has quietly become a museum while still describing itself as current is the failure this
    /// prevents — and the one hardest to notice from outside.
    /// </summary>
    public TimeSpan MaxBundleAge { get; set; } = TimeSpan.FromDays(14);

    public int MaxItemsPerBundle { get; set; } = 100;

    public int MaxExcerptLength { get; set; } = 1000;

    /// <summary>
    /// How often the directory is re-read while the host runs. Bundles are committed files rather
    /// than a feed, so this is slow on purpose: it exists to pick up a newly deployed bundle, not to
    /// poll anything.
    /// </summary>
    public TimeSpan RescanInterval { get; set; } = TimeSpan.FromMinutes(30);

    /// <summary>
    /// Platforms a user-generated item may name. Empty means the parser's own default, which is the
    /// set this project can legitimately read without authentication.
    /// </summary>
    public IList<string> AllowedPlatforms { get; } = [];

    /// <summary>
    /// Where the bundles actually are.
    /// <para>
    /// Resolved against the deployed binaries rather than the working directory or the content root.
    /// Bundles are copied into the build output, so they travel with the application into a container
    /// the same way the recorded replay stream does, and nothing that reads them keeps a dependency
    /// on the hosting environment.
    /// </para>
    /// </summary>
    internal string ResolveDirectory() => Path.IsPathRooted(Directory)
        ? Directory
        : Path.Combine(AppContext.BaseDirectory, Directory);

    internal CollectionBundleLimits ToLimits() => new(
        MaxItemsPerBundle,
        MaxExcerptLength,
        AllowedPlatforms: AllowedPlatforms.Count > 0 ? [.. AllowedPlatforms] : null);
}
