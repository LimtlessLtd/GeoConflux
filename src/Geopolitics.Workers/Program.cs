using Geopolitics.Infrastructure;
using Geopolitics.Infrastructure.Persistence;
using Geopolitics.Workers;

// Headless processing host. It runs the same pipeline as the API but registers no realtime layer,
// which is the practical demonstration that ADR 008 holds: processing does not depend on SignalR or
// on any connected browser.
//
// With --export <dir> it instead runs the recorded stream to completion and writes the result as
// static JSON, which is how the published dashboard gets real pipeline output without a backend.
var exportDirectory = ReadExportDirectory(args);
var isExport = exportDirectory is not null;

var builder = Host.CreateApplicationBuilder(args);
builder.Logging.ClearProviders();
builder.Logging.AddSimpleConsole(options => options.SingleLine = true);

if (isExport)
{
    // The export drives the queue and processor itself, so the background services must stay idle or
    // they would race it for the same work. A throwaway database keeps the export reproducible
    // instead of accumulating the results of previous runs.
    builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
    {
        ["ConnectionStrings:Geopolitics"] = $"Data Source={Path.Combine(Path.GetTempPath(), $"geoconflux-export-{Guid.NewGuid():N}.db")}",
        ["Pipeline:SourcesEnabled"] = "false",
        ["Pipeline:ProcessorEnabled"] = "false",
    });
}

builder.Services.AddGeopoliticsInfrastructure();
builder.Services.AddGeopoliticsPipeline(builder.Configuration);

var host = builder.Build();

using (var scope = host.Services.CreateScope())
{
    var initializer = scope.ServiceProvider.GetRequiredService<IDatabaseInitializer>();
    await initializer.InitializeAsync(CancellationToken.None);
}

if (isExport)
{
    return await SnapshotExporter.RunAsync(host.Services, exportDirectory!, CancellationToken.None);
}

await host.RunAsync();
return 0;

static string? ReadExportDirectory(string[] arguments)
{
    var index = Array.FindIndex(arguments, argument =>
        string.Equals(argument, "--export", StringComparison.OrdinalIgnoreCase));

    if (index < 0)
    {
        return null;
    }

    return index + 1 < arguments.Length && !arguments[index + 1].StartsWith('-')
        ? arguments[index + 1]
        : throw new ArgumentException("--export requires an output directory, for example: --export ./dist/data");
}
