using Geopolitics.Application.Operations;

namespace Geopolitics.Api.Endpoints;

/// <summary>
/// What this host holds and how it has been running.
/// <para>
/// Separate from analytics on purpose. Everything under <c>/api/analytics</c> answers a question
/// about the world this system is watching; this answers a question about the machine doing the
/// watching. They are rendered on the same tab because the second bounds the first, and they are
/// different endpoints because they would otherwise be invalidated by different events.
/// </para>
/// </summary>
public static class OperationsEndpoints
{
    public static IEndpointRouteBuilder MapOperationsEndpoints(this IEndpointRouteBuilder builder)
    {
        builder.MapGet(
            "/api/operations",
            async (IOperationsService service, CancellationToken cancellationToken) =>
                Results.Ok(await service.BuildAsync(cancellationToken)))
            .WithTags("Operations")
            .WithName("GetOperations")
            .WithSummary("What this host is holding, how much space it takes, and how fast it is filling.")
            .WithDescription(
                "Row counts and bytes measured from the store itself. A retention policy is a "
                + "decision about these numbers; publishing them is what stops it being a guess.");

        return builder;
    }
}
