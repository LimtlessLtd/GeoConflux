using System.Text.Json.Serialization;
using Geopolitics.Api;
using Geopolitics.Api.Endpoints;
using Geopolitics.Api.Realtime;
using Geopolitics.Application.Abstractions;
using Geopolitics.Application.Contracts;
using Geopolitics.Application.Pipeline;
using Geopolitics.Infrastructure;
using Geopolitics.Infrastructure.Persistence;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.SignalR;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;

// Container health probe. Handled before the host is built because it is not a mode of the
// application: it is a separate, short-lived process that asks a running one whether it is alive.
//
// It exists because the ASP.NET runtime image ships no curl or wget, so a Docker HEALTHCHECK has
// nothing to call the endpoint with. Re-running this assembly with a flag is the approach that needs
// no extra package in the image and no shell.
if (args.Contains("--health-probe", StringComparer.Ordinal))
{
    return await HealthProbe.RunAsync();
}

var builder = WebApplication.CreateBuilder(args);

// Lets this host be started by the Windows service control manager, which is what makes it survive a
// reboot without anybody logging in. It does three things and the third is the one that bites: it
// reports the service as started rather than letting Windows time out at error 1053, it routes
// lifetime events to stop and shutdown, and it sets the content root to the binary's directory —
// because a service starts in C:\Windows\System32, and without that a relative path resolves there.
//
// A no-op on any other platform and when started from a terminal, so the same build serves a
// container, a developer, and a service. See docs/operations/running-continuously.md.
builder.Host.UseWindowsService(options => options.ServiceName = "GeoConflux");

// Two orders of magnitude below the 30 MB default and far above any legitimate submission. The
// observation content cap is enforced during validation, but that runs after the body has been read,
// so this is what stops an oversized payload from being parsed before it is refused. It is set on the
// server rather than per endpoint because every request this application accepts is small JSON, and
// because the per-endpoint metadata minimal APIs expose for this is not honoured by the request
// pipeline. Note that TestServer bypasses Kestrel, so the integration tests do not exercise it.
builder.WebHost.ConfigureKestrel(options => options.Limits.MaxRequestBodySize = 256 * 1024);

builder.Logging.ClearProviders();
builder.Logging.AddJsonConsole();
builder.Services.ConfigureHttpJsonOptions(options => options.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));
builder.Services.AddProblemDetails();
builder.Services.AddOpenApi();
builder.Services.AddHealthChecks();

builder.Services.AddSubmissionRateLimiting();

builder.Services.AddSignalR()
    .AddJsonProtocol(options => options.PayloadSerializerOptions.Converters.Add(new JsonStringEnumConverter()));

builder.Services.AddGeopoliticsInfrastructure();

// Registered before AddGeopoliticsPipeline so its TryAddSingleton fallback to the null notifier does
// not win: in this host, realtime delivery is wanted.
builder.Services.AddSingleton<IIncidentNotifier, SignalRIncidentNotifier>();
builder.Services.AddGeopoliticsPipeline(builder.Configuration);

builder.Services
    .AddOpenTelemetry()
    .WithTracing(tracing => tracing
        .AddAspNetCoreInstrumentation()
        .AddSource(PipelineDiagnostics.ActivitySourceName))
    .WithMetrics(metrics => metrics
        .AddAspNetCoreInstrumentation()
        .AddMeter(PipelineDiagnostics.MeterName));

var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    app.UseDeveloperExceptionPage();
}
else
{
    app.UseExceptionHandler();
}

app.UseSecurityHeaders();
app.UseRateLimiter();
app.UseDefaultFiles();
app.UseStaticFiles();
app.MapOpenApi();

using (var scope = app.Services.CreateScope())
{
    var initializer = scope.ServiceProvider.GetRequiredService<IDatabaseInitializer>();
    await initializer.InitializeAsync(CancellationToken.None);
}

var incidents = app.MapGroup("/api/incidents").WithTags("Incidents");
incidents.MapGet(
    "/",
    async (int? take, DateTimeOffset? from, DateTimeOffset? to, IIncidentQueryService service, CancellationToken cancellationToken) =>
    {
        var response = await service.ListAsync(new IncidentSearch(take ?? 100, from, to), cancellationToken);
        return Results.Ok(response);
    })
    .WithName("ListIncidents");

incidents.MapGet(
    "/{id:guid}",
    async (Guid id, IIncidentQueryService service, CancellationToken cancellationToken) =>
    {
        var response = await service.GetByIdAsync(id, cancellationToken);
        return response is null ? Results.NotFound() : Results.Ok(response);
    })
    .WithName("GetIncident");

var spatial = app.MapGroup("/api/spatial").WithTags("Spatial");

spatial.MapGet(
    "/incidents-near",
    async (
        double lat,
        double lon,
        double? radiusKm,
        int? take,
        ISpatialQueryService service,
        CancellationToken cancellationToken) =>
    {
        if (lat is < -90 or > 90 || lon is < -180 or > 180)
        {
            return Results.BadRequest(new { error = "lat must be within -90..90 and lon within -180..180." });
        }

        var radius = Math.Clamp(radiusKm ?? 250, 1, 5000);
        var results = await service.FindIncidentsNearAsync(lat, lon, radius, take ?? 50, cancellationToken);

        return Results.Ok(new
        {
            centre = new { latitude = lat, longitude = lon },
            radiusKilometres = radius,

            // Stated rather than implied, so a caller knows the precision behind these distances.
            method = service.Method,
            count = results.Count,
            incidents = results,
        });
    })
    .WithName("FindIncidentsNear")
    .WithSummary("Incidents within a radius of a point, nearest first.");

spatial.MapGet(
    "/chokepoints",
    async (int? windowHours, ISpatialQueryService service, CancellationToken cancellationToken) =>
    {
        var hours = Math.Clamp(windowHours ?? 24, 1, 24 * 90);
        var results = await service.AnalyseChokepointsAsync(TimeSpan.FromHours(hours), cancellationToken);

        return Results.Ok(new
        {
            windowHours = hours,
            method = service.Method,

            // Said plainly in the payload, not only in the UI: proximity to a chokepoint is a
            // geographic fact about where a report was placed, not an assessment of risk to it.
            notice = "Counts describe incidents recorded within each watch radius. Proximity is not an assessment of threat.",
            chokepoints = results,
        });
    })
    .WithName("AnalyseChokepoints")
    .WithSummary("Recorded activity around each watched maritime chokepoint.");

app.MapObservationEndpoints();
app.MapAnalyticsEndpoints();
app.MapOperationsEndpoints();
app.MapSeverityEndpoints();
app.MapHub<IncidentHub>(IncidentHub.Path);

app.MapHealthChecks(
    "/api/health",
    new HealthCheckOptions
    {
        ResponseWriter = async (context, report) =>
        {
            context.Response.ContentType = "application/json";
            await context.Response.WriteAsJsonAsync(new
            {
                status = report.Status.ToString(),
                checks = report.Entries.Select(entry => new
                {
                    name = entry.Key,
                    status = entry.Value.Status.ToString(),
                    description = entry.Value.Description,
                    data = entry.Value.Data,
                }),
            });
        },
    });

// Liveness answers "is the process up", so it deliberately runs no checks: a saturated queue or a
// slow database must not cause an orchestrator to restart a process that is working correctly.
app.MapHealthChecks("/health/live", new HealthCheckOptions { Predicate = _ => false });

await app.RunAsync();
return 0;

/// <summary>
/// Asks a running instance whether it is alive, and reports the answer as an exit code.
/// </summary>
internal static class HealthProbe
{
    /// <summary>
    /// Liveness, not readiness. <c>/health/live</c> runs no checks by design, so a saturated queue or
    /// a slow database cannot cause an orchestrator to kill a process that is working correctly.
    /// </summary>
    private const string Path = "/health/live";

    public static async Task<int> RunAsync()
    {
        // The same URL the host was told to listen on, so the probe follows a port change rather
        // than needing to be kept in step with one.
        var origin = Environment.GetEnvironmentVariable("ASPNETCORE_URLS")?.Split(';')[0]
            ?.Replace("+", "localhost", StringComparison.Ordinal)
            ?.Replace("*", "localhost", StringComparison.Ordinal)
            ?? "http://localhost:8080";

        try
        {
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
            using var response = await client.GetAsync(new Uri($"{origin.TrimEnd('/')}{Path}"));
            return response.IsSuccessStatusCode ? 0 : 1;
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or UriFormatException)
        {
            // Unreachable, too slow, or misconfigured all mean the same thing to an orchestrator.
            await Console.Error.WriteLineAsync($"Health probe failed: {exception.Message}");
            return 1;
        }
    }
}

public partial class Program
{
}
