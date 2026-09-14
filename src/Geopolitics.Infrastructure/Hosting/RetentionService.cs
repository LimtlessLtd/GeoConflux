using Geopolitics.Application.Abstractions;
using Geopolitics.Infrastructure.Persistence;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Geopolitics.Infrastructure.Hosting;

/// <summary>
/// Runs the retention policy on a schedule, for as long as the host is up.
/// <para>
/// The first run waits a full interval rather than happening at startup. A host that is restarted
/// while somebody is diagnosing a failure should not delete the failures they are looking at as its
/// first act, and there is no urgency here that a day of waiting costs anything: the problem this
/// exists for takes months to arrive.
/// </para>
/// </summary>
public sealed partial class RetentionService(
    IServiceScopeFactory scopeFactory,
    IOptions<RetentionOptions> options,
    RetentionLog log,
    TimeProvider timeProvider,
    ILogger<RetentionService> logger) : BackgroundService
{
    private readonly RetentionOptions options = options.Value;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!this.options.Enabled)
        {
            LogDisabled(logger);
            return;
        }

        var interval = this.options.Interval > TimeSpan.Zero ? this.options.Interval : TimeSpan.FromHours(24);
        LogStarting(logger, this.options.Keep, interval);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(interval, timeProvider, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            await PruneAsync(stoppingToken);
        }
    }

    private async Task PruneAsync(CancellationToken cancellationToken)
    {
        try
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            var repository = scope.ServiceProvider.GetRequiredService<IRetentionRepository>();

            var now = timeProvider.GetUtcNow();
            var result = await repository.PruneAsync(now - options.Keep, cancellationToken);

            log.Record(result, now);

            if (result.Rows > 0)
            {
                LogPruned(logger, result.Observations, result.Inferences, result.ReclaimedBytes);
            }
            else
            {
                LogNothingToPrune(logger);
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // Contained like a failed poll. A retention pass that could not run is a degraded cycle,
            // and the next one tries again; taking the host down over it would trade a slowly
            // growing database for an outage.
            LogFailed(logger, exception);
        }
    }

    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "Retention is off, so nothing is deleted from the database. Read the holdings panel before turning it on.")]
    private static partial void LogDisabled(ILogger logger);

    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "Retention keeps duplicate and failed observations for {Keep} and runs every {Interval}. Nothing an incident rests on is touched.")]
    private static partial void LogStarting(ILogger logger, TimeSpan keep, TimeSpan interval);

    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "Retention removed {Observations} observation(s) and {Inferences} orphaned inference row(s), returning {ReclaimedBytes} bytes.")]
    private static partial void LogPruned(ILogger logger, long observations, long inferences, long reclaimedBytes);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Retention found nothing past the horizon.")]
    private static partial void LogNothingToPrune(ILogger logger);

    [LoggerMessage(Level = LogLevel.Error, Message = "A retention pass failed. The next scheduled pass is unaffected.")]
    private static partial void LogFailed(ILogger logger, Exception exception);
}
