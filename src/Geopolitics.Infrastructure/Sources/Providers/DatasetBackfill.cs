using Geopolitics.Application.Contracts;
using Microsoft.Extensions.Logging;

namespace Geopolitics.Infrastructure.Sources.Providers;

/// <summary>
/// Walks a dataset's history backwards, a bounded distance per poll, resuming where it stopped.
/// <para>
/// The three properties Sprint 11 asks for are each one decision here. <b>Bounded</b> is the shared
/// request budget: a poll spends a fixed number of requests and stops mid-walk without apology.
/// <b>Resumable</b> is the checkpoint, written after each window so that the next poll — or the next
/// deployment — picks the walk up rather than restarting it at the present. <b>Idempotent</b> is
/// free and was already there: every envelope goes through the same fingerprint deduplication as a
/// live poll, so re-reading a window costs requests and changes nothing.
/// </para>
/// <para>
/// The budget is shared with the live window and spent on it first, which is a priority decision
/// rather than an implementation detail. A busy day now outranks a quiet week in 2019, and a
/// deployment whose current-events window is large enough to consume the whole budget should be
/// making no backfill progress — that is the signal to raise the budget, not something to paper over
/// by giving history a reservation.
/// </para>
/// </summary>
internal static partial class DatasetBackfill
{
    /// <summary>
    /// Spends whatever is left of <paramref name="budget"/> reading history earlier than
    /// <paramref name="liveWindowStart"/>, and returns what it found.
    /// </summary>
    public static async Task<IReadOnlyList<ObservationEnvelope>> ReadAsync(
        DatasetProviderOptions settings,
        DateTimeOffset liveWindowStart,
        DatasetWindowRequest request,
        RequestBudget budget,
        IIngestionCheckpointStore checkpoints,
        string provider,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        if (settings.BackfillSince is not { } earliest)
        {
            // No backfill configured, which is the default. A deployment that has not said how far
            // back it wants history gets the live window and nothing else.
            return [];
        }

        // Never inherited from the live window if a checkpoint exists: the checkpoint is the record
        // of what has been asked, and the live window start moves forward with the clock.
        var frontier = await checkpoints.ReadAsync(provider, cancellationToken) ?? liveWindowStart;

        if (frontier <= earliest)
        {
            LogHistoryComplete(logger, provider, earliest);
            return [];
        }

        var collected = new List<ObservationEnvelope>();

        while (frontier > earliest && budget.Remaining > 0)
        {
            var start = frontier - settings.BackfillWindow;

            if (start < earliest)
            {
                start = earliest;
            }

            var window = new DatasetWindow(start, frontier);
            var result = await DatasetHistory.ReadAsync(
                window, request, budget, settings.MinimumWindow, provider, logger, cancellationToken);

            collected.AddRange(result.Envelopes);

            if (!result.Complete)
            {
                // The budget ran out partway through this window. Leaving the checkpoint where it is
                // means the next poll re-reads the part already covered, which deduplication absorbs
                // for the price of a request. Advancing it would mean never reading the rest, which
                // nothing recovers from — and the gap would be invisible, because a map cannot show
                // the events it was never offered.
                break;
            }

            frontier = start;
            await checkpoints.WriteAsync(provider, frontier, cancellationToken);
            LogWalked(logger, provider, window.Start, window.End, result.Envelopes.Count, frontier);
        }

        return collected;
    }

    [LoggerMessage(
        Level = LogLevel.Debug,
        Message = "Provider {ProviderName} has already requested its whole configured history, back to {Earliest}. Only the live window is read from now on.")]
    private static partial void LogHistoryComplete(ILogger logger, string providerName, DateTimeOffset earliest);

    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "Provider {ProviderName} backfilled {WindowStart} to {WindowEnd} and took {RecordCount} record(s). History has now been requested from {Frontier} forward.")]
    private static partial void LogWalked(
        ILogger logger,
        string providerName,
        DateTimeOffset windowStart,
        DateTimeOffset windowEnd,
        int recordCount,
        DateTimeOffset frontier);
}
