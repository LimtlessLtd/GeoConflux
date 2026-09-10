using System.Text.Json.Serialization;
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

var builder = WebApplication.CreateBuilder(args);

builder.Logging.ClearProviders();
builder.Logging.AddJsonConsole();
builder.Services.ConfigureHttpJsonOptions(options => options.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));
builder.Services.AddProblemDetails();
builder.Services.AddOpenApi();
builder.Services.AddHealthChecks();

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

app.MapObservationEndpoints();
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

app.Run();

public partial class Program
{
}
