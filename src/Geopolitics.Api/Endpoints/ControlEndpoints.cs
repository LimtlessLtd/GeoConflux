using Geopolitics.Application.Control;

namespace Geopolitics.Api.Endpoints;

/// <summary>
/// Who is assessed to hold what, on what evidence, and as of when.
/// <para>
/// Its own group rather than a corner of analytics, because it is the one endpoint in this API that
/// publishes a <em>conclusion</em> rather than a count or a record. Everything else here reports what
/// sources said or what a deterministic rule derived; this says what this system thinks, which earns
/// a boundary of its own and the response fields that go with it.
/// </para>
/// </summary>
public static class ControlEndpoints
{
    public static IEndpointRouteBuilder MapControlEndpoints(this IEndpointRouteBuilder builder)
    {
        builder.MapGet(
            "/api/control",
            async (IControlAssessmentService service, CancellationToken cancellationToken) =>
                Results.Ok(await service.BuildAsync(cancellationToken)))
            .WithTags("Control")
            .WithName("GetAssessedControl")
            .WithSummary("Who is assessed to hold each place, with the evidence behind it and its age.")
            .WithDescription(
                "Assessed per place from reports about who holds it, never from reports of fighting. "
                + "Every assessment carries the identifiers of the records behind it and the age of "
                + "the newest, so it can be checked rather than believed. Nothing is interpolated "
                + "between assessed places: this is not a front line, and the absence of one is a "
                + "property of the data rather than a rendering choice.");

        return builder;
    }
}
