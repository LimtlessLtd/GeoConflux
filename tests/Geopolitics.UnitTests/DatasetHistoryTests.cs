using Geopolitics.Application.Contracts;
using Geopolitics.Domain;
using Geopolitics.Infrastructure.Sources.Providers;
using Microsoft.Extensions.Logging.Abstractions;

namespace Geopolitics.UnitTests;

/// <summary>
/// Reading a coded dataset's history, which is a different problem from polling a feed.
/// <para>
/// A feed answers "what is new" completely, every time. ACLED and UCDP are archives, and asking one
/// for a span of time can get back an answer that was silently cut off at a row limit. Everything
/// here exists to make that case visible and bounded rather than quiet: narrowing until the provider
/// can answer, stopping at a budget, and never claiming a span was covered when it was not.
/// </para>
/// </summary>
public sealed class DatasetHistoryTests
{
    private static readonly DateTimeOffset Noon = new(2026, 6, 1, 12, 0, 0, TimeSpan.Zero);

    private static readonly TimeSpan MinimumWindow = TimeSpan.FromHours(6);

    [Fact]
    public async Task AWindowTheProviderAnswersInFullCostsOneRequest()
    {
        var provider = new RecordingProvider(_ => (Rows: 3, Truncated: false));
        var budget = new RequestBudget(10);

        var result = await ReadAsync(Window(days: 7), provider, budget);

        Assert.Single(provider.Requests);
        Assert.Equal(3, result.Envelopes.Count);
        Assert.True(result.Complete);
        Assert.Equal(9, budget.Remaining);
    }

    /// <summary>
    /// The central behaviour. A provider that says "there is more here than I sent" gets asked twice
    /// about smaller spans, until it stops saying it.
    /// </summary>
    [Fact]
    public async Task ATruncatedWindowIsNarrowedUntilTheProviderCanAnswerIt()
    {
        // Truncated above two days, answerable at or below it: a busy fortnight in one theatre.
        var provider = new RecordingProvider(window =>
            window.Length > TimeSpan.FromDays(2) ? (Rows: 50, Truncated: true) : (Rows: 4, Truncated: false));

        var result = await ReadAsync(Window(days: 8), provider, new RequestBudget(32));

        Assert.True(result.Complete);

        // 8 days truncates, both 4-day halves truncate, and all four 2-day quarters answer: seven
        // requests. Asserted as a count rather than a shape because the shape is the binary split.
        Assert.Equal(7, provider.Requests.Count);

        // Every hour of the original span ends up inside exactly one window that was answered in
        // full. This is the property that matters: narrowing must partition, not sample.
        var answered = provider.Requests.Where(window => window.Length <= TimeSpan.FromDays(2)).ToArray();
        Assert.Equal(Window(days: 8).Start, answered.Min(window => window.Start));
        Assert.Equal(Window(days: 8).End, answered.Max(window => window.End));
        Assert.Equal(TimeSpan.FromDays(8), TimeSpan.FromTicks(answered.Sum(window => window.Length.Ticks)));
    }

    /// <summary>
    /// Without a floor, a provider that truncates everything would have this splitting towards a
    /// zero-length window and spending its entire budget on a single instant.
    /// </summary>
    [Fact]
    public async Task NarrowingStopsAtTheMinimumWindowRatherThanSplittingTowardsZero()
    {
        var provider = new RecordingProvider(_ => (Rows: 50, Truncated: true));

        var result = await ReadAsync(Window(days: 1), provider, new RequestBudget(1_000));

        // Finished asking, and every request was at least the minimum window wide.
        Assert.True(result.Complete);
        Assert.All(provider.Requests, window => Assert.True(window.Length >= MinimumWindow));

        // 24 hours halved down to 6: 1 + 2 + 4 = 7 requests, and then it stops.
        Assert.Equal(7, provider.Requests.Count);
    }

    /// <summary>
    /// A window that is still truncated at the minimum is a real gap. It is reported as incomplete
    /// coverage, not smoothed over — a reader who took it for an absence of events would be reading
    /// the limits of the request as a fact about the world.
    /// </summary>
    [Fact]
    public async Task AWindowSaturatedAtTheMinimumIsKeptRatherThanDiscarded()
    {
        var provider = new RecordingProvider(_ => (Rows: 50, Truncated: true));

        var result = await ReadAsync(Window(days: 1), provider, new RequestBudget(1_000));

        // What the provider did send is kept. Half of a busy day is worth more than none of it, and
        // the fingerprint index absorbs the overlap between a window and its halves.
        Assert.NotEmpty(result.Envelopes);
    }

    [Fact]
    public async Task TheBudgetIsACeilingAndAnUnfinishedSpanSaysSo()
    {
        var provider = new RecordingProvider(window =>
            window.Length > TimeSpan.FromDays(1) ? (Rows: 50, Truncated: true) : (Rows: 2, Truncated: false));

        var result = await ReadAsync(Window(days: 16), provider, new RequestBudget(3));

        Assert.Equal(3, provider.Requests.Count);

        // The distinction the backfill depends on. Complete means "every window was asked about",
        // and a run that stopped partway must not be able to claim it.
        Assert.False(result.Complete);
    }

    /// <summary>
    /// Which three requests a budget of three buys is not arbitrary. Spending them on the earliest
    /// part of the span leaves history contiguous behind the walk; spending them at random would
    /// leave holes, and nothing goes back for a hole once the checkpoint has passed it.
    /// </summary>
    [Fact]
    public async Task AnExhaustedBudgetLeavesAContiguousSpanRatherThanHoles()
    {
        var provider = new RecordingProvider(window =>
            window.Length > TimeSpan.FromDays(1) ? (Rows: 50, Truncated: true) : (Rows: 2, Truncated: false));

        var span = Window(days: 4);
        await ReadAsync(span, provider, new RequestBudget(4));

        var answered = provider.Requests.Where(window => window.Length <= TimeSpan.FromDays(1)).ToArray();

        Assert.NotEmpty(answered);
        Assert.Equal(span.Start, answered.Min(window => window.Start));
    }

    private static Task<DatasetReadResult> ReadAsync(
        DatasetWindow window,
        RecordingProvider provider,
        RequestBudget budget) =>
        DatasetHistory.ReadAsync(
            window,
            provider.RespondAsync,
            budget,
            MinimumWindow,
            "test",
            NullLogger.Instance,
            CancellationToken.None);

    private static DatasetWindow Window(int days) => new(Noon.AddDays(-days), Noon);

    /// <summary>
    /// A provider whose answer is a function of what it was asked, so a test can describe "truncates
    /// above two days" rather than scripting a sequence of responses and hoping the order holds.
    /// </summary>
    private sealed class RecordingProvider(Func<DatasetWindow, (int Rows, bool Truncated)> answer)
    {
        public List<DatasetWindow> Requests { get; } = [];

        public Task<DatasetWindowResult> RespondAsync(DatasetWindow window, CancellationToken cancellationToken)
        {
            Requests.Add(window);
            var (rows, truncated) = answer(window);

            var envelopes = Enumerable.Range(0, rows).Select(index => new ObservationEnvelope
            {
                SourceName = "test",
                Kind = ObservationKind.ExternalEvent,
                SourceIdentifier = $"{window.Start:O}-{index}",
                Content = "A coded event.",
                Provenance = ObservationProvenance.Polled,
            });

            return Task.FromResult(new DatasetWindowResult([.. envelopes], truncated));
        }
    }
}
