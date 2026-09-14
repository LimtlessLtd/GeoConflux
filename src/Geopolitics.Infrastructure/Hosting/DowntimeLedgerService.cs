using Geopolitics.Application.Operations;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Geopolitics.Infrastructure.Hosting;

/// <summary>
/// Works out, once, whether this host has been away — before anything starts polling again.
/// <para>
/// It implements <see cref="IHostedService"/> directly rather than deriving from
/// <see cref="BackgroundService"/>, and that is the whole design. A background service returns to the
/// host at its first await, so the pump would start and poll while this was still reading; the first
/// poll writes a fresh timestamp, and the gap this exists to measure would be gone before it was
/// measured. Doing the work inside <c>StartAsync</c> means it finishes before the next hosted
/// service starts, which is also why it is registered before the pump.
/// </para>
/// </summary>
public sealed partial class DowntimeLedgerService(
    IServiceScopeFactory scopeFactory,
    ILogger<DowntimeLedgerService> logger) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            var continuity = scope.ServiceProvider.GetRequiredService<IContinuityService>();

            await continuity.RecordStartupGapAsync(cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // Contained, and this one matters more than the other containments in this project.
            // An exception out of StartAsync aborts host startup, so an unwritten ledger row would
            // take down the collection this host exists to do — trading a record of an outage for
            // an outage. The gap is lost and the host runs.
            LogNotRecorded(logger, exception);
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    [LoggerMessage(
        Level = LogLevel.Error,
        Message = "Could not work out whether this host has been away, so no downtime period was recorded for this start. Collection is unaffected.")]
    private static partial void LogNotRecorded(ILogger logger, Exception exception);
}
