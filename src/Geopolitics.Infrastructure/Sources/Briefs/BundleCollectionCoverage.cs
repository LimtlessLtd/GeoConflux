using Geopolitics.Application.Abstractions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Geopolitics.Infrastructure.Sources.Briefs;

/// <summary>
/// Reports what the committed collection runs asked and what came back.
/// <para>
/// Reads the same files the event source does, with the same parser, and takes a different thing out
/// of them: the source outcomes rather than the items. Kept separate because they answer different
/// questions and travel to different places — the items become observations, and this becomes a
/// statement about how much of the world those observations represent.
/// </para>
/// <para>
/// Expiry is deliberately not applied here, and that is the one place this parts company with the
/// event source. A bundle too old to serve as current reporting is still an accurate record of what
/// was looked at when it ran, and dropping it would replace a stated gap with an unstated one at
/// exactly the moment the dashboard has least to show.
/// </para>
/// </summary>
public sealed partial class BundleCollectionCoverage(
    IOptions<AgentBriefOptions> options,
    TimeProvider timeProvider,
    ILogger<BundleCollectionCoverage> logger) : ICollectionCoverage
{
    private readonly AgentBriefOptions options = options.Value;

    public IReadOnlyList<SourceOutcome> ReadOutcomes()
    {
        var directory = this.options.ResolveDirectory();

        if (!this.options.Enabled || !Directory.Exists(directory))
        {
            return [];
        }

        var now = timeProvider.GetUtcNow();
        var limits = this.options.ToLimits();
        var files = Directory.GetFiles(directory, "*.json", SearchOption.TopDirectoryOnly);

        // Newest run last on disk, so the list is reversed to put the most recent account first.
        Array.Sort(files, StringComparer.Ordinal);
        Array.Reverse(files);

        var outcomes = new List<SourceOutcome>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var file in files)
        {
            CollectionBundleReadResult result;

            try
            {
                result = CollectionBundleParser.Parse(File.ReadAllText(file), now, limits);
            }
            catch (Exception exception) when (exception is FormatException or IOException)
            {
                LogUnreadable(logger, Path.GetFileName(file), exception.Message);
                continue;
            }

            foreach (var source in result.Bundle.Sources)
            {
                // One row per channel, from its most recent run. Summing across runs would report a
                // channel that has been dead for a month as having produced whatever it produced
                // before it died, which is the reverse of what this is for.
                if (seen.Add(source.Channel))
                {
                    outcomes.Add(new SourceOutcome(
                        source.Channel,
                        source.Outcome,
                        source.Reason,
                        source.Read,
                        source.Matched,
                        source.Collected));
                }
            }
        }

        return outcomes;
    }

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "Collection bundle '{File}' could not be read for coverage: {Reason}")]
    private static partial void LogUnreadable(ILogger logger, string file, string reason);
}
