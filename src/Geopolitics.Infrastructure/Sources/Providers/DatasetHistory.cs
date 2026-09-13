using Geopolitics.Application.Contracts;
using Microsoft.Extensions.Logging;

namespace Geopolitics.Infrastructure.Sources.Providers;

/// <summary>One bounded slice of a dataset's history, half-open: <c>[Start, End)</c>.</summary>
internal readonly record struct DatasetWindow(DateTimeOffset Start, DateTimeOffset End)
{
    public TimeSpan Length => End - Start;

    /// <summary>Halves the window. Used when a provider could not answer the whole of it at once.</summary>
    public (DatasetWindow Earlier, DatasetWindow Later) Split()
    {
        var midpoint = Start.AddTicks(Length.Ticks / 2);
        return (new DatasetWindow(Start, midpoint), new DatasetWindow(midpoint, End));
    }

    public override string ToString() => $"{Start:yyyy-MM-dd HH:mm}Z to {End:yyyy-MM-dd HH:mm}Z";
}

/// <param name="Envelopes">What the provider returned for the window.</param>
/// <param name="Truncated">
/// Whether the provider signalled that more exists for this window than it sent. Each adapter reads
/// this from what its own API documents rather than from a shared guess: UCDP publishes a link to a
/// next page, ACLED publishes a row count that can be compared against the requested limit.
/// </param>
internal sealed record DatasetWindowResult(IReadOnlyList<ObservationEnvelope> Envelopes, bool Truncated);

internal delegate Task<DatasetWindowResult> DatasetWindowRequest(
    DatasetWindow window,
    CancellationToken cancellationToken);

/// <param name="Envelopes">Everything read across the span.</param>
/// <param name="Complete">
/// Whether every window in the span was actually requested before the budget ran out.
/// <para>
/// Note what this does and does not claim. It says the span was fully <em>asked about</em>; it does
/// not promise the provider answered each question fully, because a window narrowed to the minimum
/// and still truncated is recorded as incomplete and logged. The distinction matters to exactly one
/// caller: the backfill may only move its checkpoint past a span it finished asking about, since a
/// span abandoned half-read would otherwise be skipped permanently.
/// </para>
/// </param>
internal sealed record DatasetReadResult(IReadOnlyList<ObservationEnvelope> Envelopes, bool Complete);

/// <summary>
/// How many requests one poll may make. The bound Sprint 11 is named after.
/// <para>
/// Not thread-safe, and does not need to be: a budget belongs to a single poll of a single adapter,
/// and polls of one adapter do not overlap.
/// </para>
/// </summary>
internal sealed class RequestBudget(int total)
{
    public int Remaining { get; private set; } = Math.Max(1, total);

    public bool TrySpend()
    {
        if (Remaining <= 0)
        {
            return false;
        }

        Remaining--;
        return true;
    }
}

/// <summary>
/// Reads a span of a dataset's history in bounded pieces, narrowing where the provider could not
/// answer in one go.
/// <para>
/// This is the shape ACLED and UCDP need and no feed adapter here has ever needed. An RSS feed is a
/// window onto the present: ask it what is new, get everything it has, done. A coded conflict dataset
/// is an archive with years behind it, and the only honest way to read one is to ask for a bounded
/// slice of time and find out whether the answer was complete.
/// </para>
/// <para>
/// Narrowing by <em>time</em> rather than by page number is a deliberate choice about what may be
/// assumed. Page indexing is off-by-one-able and neither provider documents its base publicly enough
/// to rely on; getting it wrong would silently skip a page, and silently skipping data is the worst
/// failure available to this system. A date window is unambiguous, both APIs document filtering on
/// one, and both are already pinned by recorded fixtures doing exactly that. A short answer to a
/// narrow question also proves its own completeness, which no page number does.
/// </para>
/// <para>
/// A truncated window's rows are kept rather than discarded before its halves are requested. The
/// halves will return those same rows again, and that costs nothing: the polling source suppresses
/// repeats within a run, and the fingerprint check rejects them against the database. Throwing them
/// away would only risk losing rows if the budget ran out mid-narrowing.
/// </para>
/// </summary>
internal static partial class DatasetHistory
{
    /// <summary>
    /// Reads <paramref name="window"/>, splitting it where the provider says it sent less than it
    /// holds, and stopping when the budget or the minimum window size runs out.
    /// </summary>
    public static async Task<DatasetReadResult> ReadAsync(
        DatasetWindow window,
        DatasetWindowRequest request,
        RequestBudget budget,
        TimeSpan minimumWindow,
        string provider,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        var collected = new List<ObservationEnvelope>();

        // Oldest-first, so that a run which exhausts its budget has spent it on the earliest part of
        // the span rather than scattering it. For the backfill walk that means history fills in
        // contiguously from one end instead of acquiring holes that nothing later goes back for.
        var pending = new Stack<DatasetWindow>();
        pending.Push(window);

        while (pending.Count > 0)
        {
            if (!budget.TrySpend())
            {
                LogBudgetExhausted(logger, provider, pending.Count);
                break;
            }

            var current = pending.Pop();
            var result = await request(current, cancellationToken);
            collected.AddRange(result.Envelopes);

            if (!result.Truncated)
            {
                continue;
            }

            if (current.Length <= minimumWindow)
            {
                // Narrowed as far as it is allowed to go and the provider still has more. Said out
                // loud rather than passed over: this window is genuinely incomplete, and a reader
                // who assumed otherwise would be reading a gap as an absence of events.
                LogWindowSaturated(logger, provider, current.Start, current.End, result.Envelopes.Count);
                continue;
            }

            var (earlier, later) = current.Split();
            pending.Push(later);
            pending.Push(earlier);
        }

        return new DatasetReadResult(collected, pending.Count == 0);
    }

    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "Provider {ProviderName} spent its whole request budget for this poll with {RemainingWindows} window(s) still unread. They are attempted again on the next poll.")]
    private static partial void LogBudgetExhausted(ILogger logger, string providerName, int remainingWindows);

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "Provider {ProviderName} reported more data between {WindowStart} and {WindowEnd} than it returned, and the window cannot be narrowed further. {ReturnedCount} record(s) were taken and this window is incomplete.")]
    private static partial void LogWindowSaturated(
        ILogger logger,
        string providerName,
        DateTimeOffset windowStart,
        DateTimeOffset windowEnd,
        int returnedCount);
}
