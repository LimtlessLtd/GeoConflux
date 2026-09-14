using Geopolitics.Infrastructure;
using Geopolitics.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Geopolitics.UnitTests;

/// <summary>
/// Where the host opens its database when nobody said absolutely.
/// <para>
/// SQLite resolves a relative <c>Data Source</c> against the process working directory, and the
/// shipped setting is the relative <c>geopolitics.db</c>. That is harmless for <c>dotnet run</c> and
/// wrong for a Windows service, which starts in <c>C:\Windows\System32</c>: the host would create an
/// empty database there, report itself healthy, and hold none of the history it was left running to
/// accumulate. Nothing fails, which is what makes it worth a test.
/// </para>
/// <para>
/// Exercised through the real registration rather than against the helper, because the thing being
/// asserted is what the composed application opens.
/// </para>
/// </summary>
public sealed class DatabaseLocationTests
{
    private static readonly string ContentRoot =
        OperatingSystem.IsWindows() ? @"C:\geoconflux-content-root" : "/srv/geoconflux";

    [Fact]
    public void ARelativeDatabasePathIsAnchoredToTheContentRootRatherThanTheWorkingDirectory()
    {
        var dataSource = DataSourceFor("Data Source=geopolitics.db");

        Assert.True(Path.IsPathRooted(dataSource));
        Assert.Equal(Path.Combine(ContentRoot, "geopolitics.db"), dataSource);
    }

    [Fact]
    public void AnAbsolutePathIsLeftExactlyAsWritten()
    {
        // A deployment that named a data volume meant it. Re-rooting that would move the database
        // out from under a host that had been told precisely where to keep it.
        var absolute = Path.Combine(Path.GetTempPath(), "named-explicitly.db");

        Assert.Equal(absolute, DataSourceFor($"Data Source={absolute}"));
    }

    [Fact]
    public void AnInMemoryDatabaseIsNotTurnedIntoAFileOnDisk()
    {
        // ":memory:" and Mode=Memory are not paths. Rooting either would change which database the
        // host opens, and the change would be from "no file" to "a file", which is not subtle in
        // consequence and is entirely silent at the point it happens.
        Assert.Equal(":memory:", DataSourceFor("Data Source=:memory:"));
        Assert.Equal("shared-store", DataSourceFor("Data Source=shared-store;Mode=Memory;Cache=Shared"));
    }

    private static string DataSourceFor(string connectionString)
    {
        var services = new ServiceCollection();

        services.AddSingleton<IConfiguration>(new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:Geopolitics"] = connectionString,
            })
            .Build());

        services.AddSingleton<IHostEnvironment>(new StubEnvironment { ContentRootPath = ContentRoot });
        services.AddGeopoliticsInfrastructure();

        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();

        var context = scope.ServiceProvider.GetRequiredService<GeopoliticsDbContext>();
        return ((SqliteConnection)context.Database.GetDbConnection()).DataSource;
    }

    private sealed class StubEnvironment : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = "Testing";

        public string ApplicationName { get; set; } = "Geopolitics.Tests";

        public string ContentRootPath { get; set; } = string.Empty;

        public Microsoft.Extensions.FileProviders.IFileProvider ContentRootFileProvider { get; set; } =
            new Microsoft.Extensions.FileProviders.NullFileProvider();
    }
}
