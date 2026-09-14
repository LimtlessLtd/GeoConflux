using Geopolitics.Application.Abstractions;
using Geopolitics.Application.Analytics;

namespace Geopolitics.Application.Conflicts;

/// <param name="Window">The window these figures cover.</param>
/// <param name="Conflicts">One entry per conflict that produced reports, busiest first.</param>
/// <param name="Registered">How many conflicts the register holds, which is the denominator of the next figure.</param>
/// <param name="Seen">How many of them this system saw anything about at all.</param>
/// <param name="Unassigned">Reports in the window that belong to no conflict.</param>
/// <param name="Undecided">Reports inside the geography of a conflict that nothing in them identified.</param>
/// <param name="Provenance">Where the register came from, stated beside every count derived from it.</param>
/// <param name="Note">What these counts do and do not establish.</param>
/// <param name="Truncated">Whether the window held more reports than the sample cap allowed.</param>
public sealed record ConflictActivityReport(
    string Window,
    IReadOnlyList<ConflictTempo> Conflicts,
    int Registered,
    int Seen,
    int Unassigned,
    int Undecided,
    string Provenance,
    string Note,
    bool Truncated);

public interface IConflictActivityService
{
    Task<ConflictActivityReport> BuildAsync(AnalyticsWindow window, CancellationToken cancellationToken);
}

/// <summary>
/// What this system saw about each conflict in a window, and what that is worth.
/// <para>
/// Two properties matter more than any number it produces. <b>Every count carries the number of
/// sources that produced it</b>, because a count of reports on its own moves when the world changes
/// and equally when the reporting does, and a reader cannot tell which. And <b>each conflict is
/// compared only against itself</b>: "Ukraine 72, Tigray 9" reads as "Tigray is eight times quieter"
/// when it may mean nobody is reporting Tigray, so the baseline is always this conflict's own
/// previous window and never another conflict's anything.
/// </para>
/// </summary>
public sealed class ConflictActivityService(
    IConflictActivityRepository repository,
    IConflictRegister register,
    TimeProvider timeProvider) : IConflictActivityService
{
    /// <summary>
    /// How many reports one window's tally will read. Generous against any volume this runs at, and
    /// present so a runaway ingest cannot turn a page request into a full table scan held in memory.
    /// </summary>
    public const int MaxSample = 20000;

    /// <summary>
    /// Said once, beside the table, because every figure in it is open to the same misreading: that a
    /// conflict low in the list is a conflict that is quiet.
    /// </summary>
    private const string CountsNote =
        "These count reports this system received, not events that happened, and a report can belong "
        + "to more than one conflict — so the rows do not sum to the total. Each conflict is compared "
        + "only against its own previous window. Comparing two conflicts' counts against each other "
        + "would measure which of them is better covered, which is not the same question.";

    public async Task<ConflictActivityReport> BuildAsync(
        AnalyticsWindow window,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(window);

        var now = timeProvider.GetUtcNow();
        var start = now - window.Duration;

        // The baseline is the window immediately before this one, of the same length. Same length
        // because a comparison between seven days and thirty is arithmetic about calendars, and
        // immediately before because a conflict's own recent past is the only baseline that does not
        // smuggle in a judgement about which conflicts are comparable.
        var previousStart = start - window.Duration;

        var current = await repository.SampleAsync(start, now, MaxSample, cancellationToken);
        var previous = await repository.SampleAsync(previousStart, start, MaxSample, cancellationToken);

        var currentCounts = Tally(current.Observations);
        var previousCounts = Tally(previous.Observations);

        var conflicts = new List<ConflictTempo>(currentCounts.Count);

        foreach (var entry in currentCounts)
        {
            var name = register.TryGet(entry.Key, out var conflict) ? conflict.Name : entry.Key;

            conflicts.Add(ConflictTempoAssessment.Assess(
                entry.Key,
                name,
                entry.Value,
                previousCounts.GetValueOrDefault(entry.Key, ConflictWindowCounts.Empty)));
        }

        return new ConflictActivityReport(
            window.Token,
            [.. conflicts
                .OrderByDescending(tempo => tempo.Current.Observations)
                .ThenBy(tempo => tempo.Name, StringComparer.Ordinal)],
            register.All.Count,
            currentCounts.Count,
            current.Observations.Count(sample => sample.ConflictKeys.Count == 0 && sample.CandidateCount == 0),
            current.Observations.Count(sample => sample.ConflictKeys.Count == 0 && sample.CandidateCount > 0),
            register.Provenance,
            CountsNote,
            current.Truncated || previous.Truncated);
    }

    /// <summary>
    /// Counts per conflict, and the deliberate double-count in it. A report belonging to two conflicts
    /// is counted in both, because it is evidence about both — which is precisely why the rows cannot
    /// be added up and why the note above says so.
    /// </summary>
    private static Dictionary<string, ConflictWindowCounts> Tally(
        IReadOnlyList<ConflictObservationSample> observations)
    {
        var reports = new Dictionary<string, int>(StringComparer.Ordinal);
        var sources = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        var incidents = new Dictionary<string, HashSet<Guid>>(StringComparer.Ordinal);

        foreach (var observation in observations)
        {
            foreach (var key in observation.ConflictKeys)
            {
                reports[key] = reports.GetValueOrDefault(key) + 1;

                if (!sources.TryGetValue(key, out var contributors))
                {
                    sources[key] = contributors = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                }

                contributors.Add(observation.SourceName);

                if (observation.IncidentId is { } incidentId)
                {
                    if (!incidents.TryGetValue(key, out var joined))
                    {
                        incidents[key] = joined = [];
                    }

                    joined.Add(incidentId);
                }
            }
        }

        return reports.ToDictionary(
            entry => entry.Key,
            entry => new ConflictWindowCounts(
                entry.Value,
                sources[entry.Key].Count,
                incidents.TryGetValue(entry.Key, out var joined) ? joined.Count : 0),
            StringComparer.Ordinal);
    }
}
