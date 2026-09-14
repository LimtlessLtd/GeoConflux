using Geopolitics.Application.Operations;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

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
public sealed class DowntimeLedgerService(IServiceScopeFactory scopeFactory) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var continuity = scope.ServiceProvider.GetRequiredService<IContinuityService>();

        await continuity.RecordStartupGapAsync(cancellationToken);
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
