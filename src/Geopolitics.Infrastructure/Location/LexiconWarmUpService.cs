using System.Diagnostics;
using Geopolitics.Application.Abstractions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Geopolitics.Infrastructure.Location;

/// <summary>
/// Builds the place lexicon while the host is starting, rather than inside whichever request happens
/// to need it first.
/// <para>
/// The lexicon is static and built on first touch, which was free when it was two hundred entries and
/// is not now: 130,000 places across three layers have to be merged, disambiguated and indexed, and
/// that costs over a second. Left lazy, the bill lands on the first observation to reach the resolver
/// — a request that then appears to take a second and a half for no reason a log would explain, and
/// which pushed an integration test past its polling budget on a slower machine.
/// </para>
/// <para>
/// Doing it here makes the cost a startup cost, which is where it belongs and where it is honest: the
/// host reports itself started once it can actually serve, rather than reporting ready and then
/// stalling. It also means the figure is logged, so the day somebody adds a zero to the lexicon the
/// cost is visible rather than mysterious.
/// </para>
/// </summary>
public sealed partial class LexiconWarmUpService(
    ILogger<LexiconWarmUpService> logger,
    IConflictRegister conflicts) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();

        // Touching either entry point builds the whole thing. Resolving a name the curated layer holds
        // asserts that the merge produced something usable, rather than only that it did not throw.
        var ready = Gazetteer.TryResolve("Kyiv", out _);

        // The conflict register is built on the lexicon, so it is warmed here rather than separately:
        // it resolves eight thousand coded place names through the gazetteer that has just finished
        // loading, and doing that inside whichever request arrives first would move a second of work
        // somewhere no log would explain it.
        var registered = conflicts.All.Count;

        stopwatch.Stop();

        LogWarmedUp(
            logger,
            stopwatch.ElapsedMilliseconds,
            GlobalPlaces.All.Count,
            TheatrePlaces.All.Count,
            Gazetteer.SearchTermCount,
            Gazetteer.AmbiguousSourcedNames,
            registered);

        if (!ready)
        {
            // Nothing resolves, so nothing will ever be placed. Better to say so at startup than to
            // let every observation fail its geocode one at a time.
            throw new InvalidOperationException(
                "The place lexicon built but resolves nothing, so no observation could be placed.");
        }

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "Place lexicon ready in {ElapsedMilliseconds} ms: {GlobalPlaces} global places, "
            + "{TheatrePlaces} theatre places, {SearchTerms} spellings searched for in prose, "
            + "{ContestedNames} names held for context to settle, {Conflicts} conflicts registered.")]
    private static partial void LogWarmedUp(
        ILogger logger,
        long elapsedMilliseconds,
        int globalPlaces,
        int theatrePlaces,
        int searchTerms,
        int contestedNames,
        int conflicts);
}
