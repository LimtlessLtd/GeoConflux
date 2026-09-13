using Geopolitics.Infrastructure.Persistence;
using Geopolitics.Infrastructure.Sources.Providers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;

namespace Geopolitics.UnitTests;

/// <summary>
/// The durable half of a resumable backfill, against a real database rather than a fake.
/// <para>
/// A fake store would verify the walk's arithmetic and nothing about the thing that actually has to
/// survive a restart. What is under test here is persistence and the one rule the store enforces on
/// its own behalf: a frontier that only ever moves earlier.
/// </para>
/// </summary>
public sealed class IngestionCheckpointStoreTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 6, 1, 12, 0, 0, TimeSpan.Zero);

    private readonly string databasePath = Path.Combine(
        Path.GetTempPath(),
        $"geoconflux-checkpoints-{Guid.NewGuid():N}.db");

    private readonly ServiceProvider services;

    public IngestionCheckpointStoreTests()
    {
        var collection = new ServiceCollection();
        collection.AddDbContext<GeopoliticsDbContext>(options => options.UseSqlite($"Data Source={databasePath}"));
        collection.AddSingleton<TimeProvider>(new FakeTimeProvider(Now));
        collection.AddSingleton<IIngestionCheckpointStore, IngestionCheckpointStore>();

        services = collection.BuildServiceProvider();

        using var scope = services.CreateScope();
        scope.ServiceProvider.GetRequiredService<GeopoliticsDbContext>().Database.EnsureCreated();
    }

    private IIngestionCheckpointStore Store => services.GetRequiredService<IIngestionCheckpointStore>();

    [Fact]
    public async Task ASourceThatHasNeverRunHasNoFrontier()
    {
        // Null rather than a default date. "Never asked" and "asked back to the epoch" are different
        // facts, and the walk starts from the live window only in the first of them.
        Assert.Null(await Store.ReadAsync("acled", CancellationToken.None));
    }

    [Fact]
    public async Task AFrontierSurvivesBeingWrittenAndReadBackByADifferentScope()
    {
        var reached = Now.AddDays(-90);

        await Store.WriteAsync("acled", reached, CancellationToken.None);

        Assert.Equal(reached, await Store.ReadAsync("acled", CancellationToken.None));
    }

    [Fact]
    public async Task EachSourceKeepsItsOwnFrontier()
    {
        await Store.WriteAsync("acled", Now.AddDays(-90), CancellationToken.None);
        await Store.WriteAsync("ucdp", Now.AddDays(-10), CancellationToken.None);

        Assert.Equal(Now.AddDays(-90), await Store.ReadAsync("acled", CancellationToken.None));
        Assert.Equal(Now.AddDays(-10), await Store.ReadAsync("ucdp", CancellationToken.None));
    }

    /// <summary>
    /// A frontier that could move forward would undo history already requested, and the walk would
    /// then spend its next polls re-reading a span it had covered rather than getting any further
    /// back. A shortened backfill window, a corrected clock, or two polls landing out of order are
    /// all ordinary ways to arrive here.
    /// </summary>
    [Fact]
    public async Task AFrontierOnlyEverMovesEarlier()
    {
        var reached = Now.AddDays(-90);

        await Store.WriteAsync("acled", reached, CancellationToken.None);
        await Store.WriteAsync("acled", Now.AddDays(-10), CancellationToken.None);

        Assert.Equal(reached, await Store.ReadAsync("acled", CancellationToken.None));

        await Store.WriteAsync("acled", Now.AddDays(-120), CancellationToken.None);

        Assert.Equal(Now.AddDays(-120), await Store.ReadAsync("acled", CancellationToken.None));
    }

    public void Dispose()
    {
        services.Dispose();
        SqliteConnectionPool.Clear();

        if (File.Exists(databasePath))
        {
            File.Delete(databasePath);
        }
    }

    /// <summary>
    /// SQLite keeps connections pooled, and a pooled connection holds the file open past disposal of
    /// the provider. Without this the delete above fails on Windows and leaves a temp file per run.
    /// </summary>
    private static class SqliteConnectionPool
    {
        public static void Clear() => Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
    }
}
