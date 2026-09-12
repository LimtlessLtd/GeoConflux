using Geopolitics.Application.Abstractions;
using Geopolitics.Application.Contracts;
using Geopolitics.Application.Pipeline;
using Geopolitics.Domain;
using Geopolitics.Infrastructure.Location;
using Geopolitics.UnitTests.Fakes;
using Microsoft.Extensions.Logging.Abstractions;

namespace Geopolitics.UnitTests;

/// <summary>
/// Pins who is allowed to state a coordinate.
/// <para>
/// ADR 005 says a language model may name a place and only a deterministic resolver may turn that
/// name into a position. The rule held everywhere except one door: <c>POST /api/observations</c>
/// accepted a latitude and longitude from any caller on the network, and the resolver honoured them
/// as source-provided — an exact position at 0.95 confidence, from an anonymous stranger, in the
/// default configuration.
/// </para>
/// <para>
/// Adding an OSINT agent as a submitter is what made that load-bearing, since an agent reading prose
/// is precisely the actor the ADR was written about. These tests are the fix's teeth.
/// </para>
/// </summary>
public sealed class CoordinateAuthorityTests
{
    private static readonly GazetteerLocationResolver Resolver =
        new(NullLogger<GazetteerLocationResolver>.Instance);

    [Theory]
    [InlineData(ObservationKind.Satellite)]
    [InlineData(ObservationKind.ExternalEvent)]
    public void AMeasurementProviderMayStateItsOwnPosition(ObservationKind kind) =>
        Assert.True(kind.MayDeclareCoordinates());

    [Theory]
    [InlineData(ObservationKind.News)]
    [InlineData(ObservationKind.Manual)]
    [InlineData(ObservationKind.Replay)]
    [InlineData(ObservationKind.Unknown)]
    public void AnAccountOfEventsMayNot(ObservationKind kind) =>
        Assert.False(kind.MayDeclareCoordinates());

    [Fact]
    public async Task AnObservationDeclaringAForbiddenCoordinateIsRefusedWithAReason()
    {
        var queue = new CapturingQueue();
        using var diagnostics = new PipelineDiagnostics(new TestMeterFactory());
        var service = new ObservationIngestionService(
            queue,
            diagnostics,
            NullLogger<ObservationIngestionService>.Instance);

        var result = await service.IngestAsync(
            new ObservationEnvelope
            {
                SourceName = "collected:example",
                Kind = ObservationKind.News,
                Content = "A report naming somewhere.",
                DeclaredLatitude = 12.585,
                DeclaredLongitude = 43.334,
            },
            CancellationToken.None);

        Assert.False(result.Accepted);
        Assert.Contains("may not declare coordinates", result.RejectionReason!, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(queue.Enqueued);
    }

    [Fact]
    public async Task ACollectedReportEarnsItsPositionFromTheGazetteerAndNotFromItself()
    {
        var resolution = await Resolver.ResolveAsync(
            new LocationResolutionRequest("باب المندب", null, null, null),
            CancellationToken.None);

        Assert.NotNull(resolution.Location);
        Assert.Equal(LocationResolutionMethod.Gazetteer, resolution.Method);
        Assert.Equal("Bab-el-Mandeb", resolution.Location!.Name);
    }

    [Fact]
    public async Task AnUnrecognisedPlaceStaysUnplacedRatherThanBeingInvented()
    {
        var resolution = await Resolver.ResolveAsync(
            new LocationResolutionRequest("Somewhere Nobody Has Heard Of", null, null, null),
            CancellationToken.None);

        Assert.Null(resolution.Location);
    }

    /// <summary>
    /// A precision describes a coordinate, so without one it describes nothing.
    /// <para>
    /// This closes the same door from the other side. A caller that cannot supply a coordinate could
    /// otherwise supply a precision for the one the resolver is about to derive from a place name —
    /// asserting how good somebody else's work is about to be, which is a claim it has no standing to
    /// make and which would travel to the reader as though the source had made it.
    /// </para>
    /// </summary>
    [Fact]
    public async Task ADeclaredPrecisionWithoutDeclaredCoordinatesIsRefused()
    {
        var queue = new CapturingQueue();
        using var diagnostics = new PipelineDiagnostics(new TestMeterFactory());
        var service = new ObservationIngestionService(
            queue,
            diagnostics,
            NullLogger<ObservationIngestionService>.Instance);

        var result = await service.IngestAsync(
            new ObservationEnvelope
            {
                SourceName = "acled",

                // A kind that is allowed coordinates, so the refusal is about the missing pair rather
                // than about who is asking.
                Kind = ObservationKind.ExternalEvent,
                Content = "An event with a precision but no position.",
                DeclaredPrecision = LocationPrecision.Settlement,
            },
            CancellationToken.None);

        Assert.False(result.Accepted);
        Assert.Contains("declared coordinates", result.RejectionReason!, StringComparison.Ordinal);
        Assert.Empty(queue.Enqueued);
    }

    private sealed class CapturingQueue : IObservationQueueWriter
    {
        public List<ObservationEnvelope> Enqueued { get; } = [];

        public ValueTask EnqueueAsync(ObservationEnvelope envelope, CancellationToken cancellationToken)
        {
            Enqueued.Add(envelope);
            return ValueTask.CompletedTask;
        }

        public void Complete()
        {
        }
    }
}
