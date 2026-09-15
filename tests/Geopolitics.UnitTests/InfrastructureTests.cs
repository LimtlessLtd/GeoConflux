using Geopolitics.Application.Abstractions;
using Geopolitics.Application.Pipeline;
using Geopolitics.Domain;
using Geopolitics.Infrastructure.Location;
using Geopolitics.Infrastructure.Queue;
using Geopolitics.Infrastructure.Sources;
using Geopolitics.UnitTests.Fakes;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Geopolitics.UnitTests;

public sealed class GazetteerLocationResolverTests
{
    private readonly GazetteerLocationResolver resolver = new(NullLogger<GazetteerLocationResolver>.Instance);

    [Fact]
    public async Task ProviderSuppliedCoordinatesOutrankAGazetteerLookup()
    {
        var resolution = await resolver.ResolveAsync(
            new LocationResolutionRequest("Bab-el-Mandeb", 12.61, 43.41, "YE"),
            CancellationToken.None);

        Assert.Equal(LocationResolutionMethod.SourceProvided, resolution.Method);
        Assert.Equal(12.61, resolution.Location!.Latitude);
        Assert.Equal(43.41, resolution.Location.Longitude);
    }

    [Fact]
    public async Task AKnownPlaceNameResolvesFromTheLocalGazetteer()
    {
        var resolution = await resolver.ResolveAsync(
            new LocationResolutionRequest("Strait of Hormuz", null, null, null),
            CancellationToken.None);

        Assert.Equal(LocationResolutionMethod.Gazetteer, resolution.Method);
        Assert.Equal("Strait of Hormuz", resolution.Location!.Name);
    }

    [Theory]
    [InlineData("bab el mandeb")]
    [InlineData("BAB-EL-MANDEB")]
    [InlineData("Bab al-Mandab")]
    public async Task PunctuationCasingAndCommonAliasesAllResolve(string name)
    {
        var resolution = await resolver.ResolveAsync(
            new LocationResolutionRequest(name, null, null, null),
            CancellationToken.None);

        Assert.True(resolution.IsResolved, $"'{name}' should resolve.");
        Assert.Equal("Bab-el-Mandeb", resolution.Location!.Name);
    }

    [Fact]
    public async Task AnUnknownPlaceIsReportedUnresolvedRatherThanGuessed()
    {
        var resolution = await resolver.ResolveAsync(
            new LocationResolutionRequest("Somewhere Unmapped", null, null, null),
            CancellationToken.None);

        // ADR 005: coordinates are never invented to keep the map populated.
        Assert.False(resolution.IsResolved);
        Assert.Null(resolution.Location);
        Assert.Equal(0, resolution.Confidence);
        Assert.NotNull(resolution.FailureReason);
    }

    [Fact]
    public async Task NoNameAndNoCoordinatesResolvesToNothing()
    {
        var resolution = await resolver.ResolveAsync(
            new LocationResolutionRequest(null, null, null, null),
            CancellationToken.None);

        Assert.False(resolution.IsResolved);
    }

    [Fact]
    public async Task AGazetteerCentroidIsLessConfidentThanAProviderFix()
    {
        var gazetteer = await resolver.ResolveAsync(
            new LocationResolutionRequest("Beirut", null, null, null),
            CancellationToken.None);
        var provided = await resolver.ResolveAsync(
            new LocationResolutionRequest("Beirut", 33.9, 35.5, "LB"),
            CancellationToken.None);

        Assert.True(gazetteer.Confidence < provided.Confidence);
    }
}

public sealed class ChannelObservationBufferTests
{
    [Fact]
    public async Task ItemsAreDeliveredToConsumersInOrder()
    {
        var buffer = new ChannelObservationBuffer(Options.Create(new PipelineOptions { QueueCapacity = 8 }));

        await buffer.EnqueueAsync(PipelineTestHarness.Envelope("first", sourceIdentifier: "1"), CancellationToken.None);
        await buffer.EnqueueAsync(PipelineTestHarness.Envelope("second", sourceIdentifier: "2"), CancellationToken.None);
        buffer.Complete();

        var received = new List<string>();
        await foreach (var envelope in buffer.DequeueAllAsync(CancellationToken.None))
        {
            received.Add(envelope.Content);
        }

        Assert.Equal(["first", "second"], received);
        Assert.Equal(2, buffer.TotalEnqueued);
    }

    [Fact]
    public async Task AFullQueueMakesProducersWaitInsteadOfGrowingWithoutBound()
    {
        var buffer = new ChannelObservationBuffer(Options.Create(new PipelineOptions { QueueCapacity = 1 }));
        await buffer.EnqueueAsync(PipelineTestHarness.Envelope("first", sourceIdentifier: "1"), CancellationToken.None);

        var blocked = buffer.EnqueueAsync(
            PipelineTestHarness.Envelope("second", sourceIdentifier: "2"),
            CancellationToken.None).AsTask();

        Assert.False(blocked.IsCompleted, "The second write should be waiting on a full queue.");
        Assert.Equal(1, buffer.Depth);

        // Draining one item releases the waiting producer: that is backpressure working.
        var enumerator = buffer.DequeueAllAsync(CancellationToken.None).GetAsyncEnumerator();
        Assert.True(await enumerator.MoveNextAsync());
        await blocked;
        await enumerator.DisposeAsync();
    }

    [Fact]
    public async Task CompletionLetsConsumersDrainAndExit()
    {
        var buffer = new ChannelObservationBuffer(Options.Create(new PipelineOptions { QueueCapacity = 4 }));
        await buffer.EnqueueAsync(PipelineTestHarness.Envelope("only", sourceIdentifier: "1"), CancellationToken.None);
        buffer.Complete();

        var count = 0;
        await foreach (var _ in buffer.DequeueAllAsync(CancellationToken.None))
        {
            count++;
        }

        // The loop terminates rather than waiting forever, which is what makes shutdown graceful.
        Assert.Equal(1, count);
    }

    [Fact]
    public async Task CancellationStopsAWaitingConsumer()
    {
        var buffer = new ChannelObservationBuffer(Options.Create(new PipelineOptions { QueueCapacity = 4 }));
        using var cancellation = new CancellationTokenSource();

        var consumer = Task.Run(async () =>
        {
            await foreach (var _ in buffer.DequeueAllAsync(cancellation.Token))
            {
                // Intentionally empty: the test is about how this loop ends.
            }
        });

        await cancellation.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => consumer);
    }
}

public sealed class ObservationIngestionServiceTests
{
    [Fact]
    public async Task AValidEnvelopeIsQueued()
    {
        var (service, buffer) = Build();

        var result = await service.IngestAsync(
            PipelineTestHarness.Envelope("A valid report.", sourceIdentifier: "1"),
            CancellationToken.None);

        Assert.True(result.Accepted);
        Assert.Equal(1, buffer.Depth);
    }

    [Theory]
    [InlineData("", "content")]
    [InlineData("source", "")]
    public async Task MissingRequiredFieldsAreRejectedBeforeTheQueue(string sourceName, string content)
    {
        var (service, buffer) = Build();

        var result = await service.IngestAsync(
            new Application.Contracts.ObservationEnvelope
            {
                SourceName = sourceName,
                Kind = ObservationKind.Manual,
                Content = content,
            },
            CancellationToken.None);

        Assert.False(result.Accepted);
        Assert.Equal(0, buffer.Depth);
    }

    [Fact]
    public async Task AHalfSuppliedCoordinatePairIsRejected()
    {
        var (service, _) = Build();

        var result = await service.IngestAsync(
            new Application.Contracts.ObservationEnvelope
            {
                SourceName = "manual",
                Kind = ObservationKind.Manual,
                Content = "A report with only a latitude.",
                DeclaredLatitude = 12.5,
            },
            CancellationToken.None);

        // Accepting this would silently place the report on the prime meridian.
        Assert.False(result.Accepted);
        Assert.Contains("both", result.RejectionReason!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task OutOfRangeCoordinatesAreRejected()
    {
        var (service, _) = Build();

        var result = await service.IngestAsync(
            new Application.Contracts.ObservationEnvelope
            {
                SourceName = "manual",
                Kind = ObservationKind.Manual,
                Content = "A report from an impossible place.",
                DeclaredLatitude = 999,
                DeclaredLongitude = 12,
            },
            CancellationToken.None);

        Assert.False(result.Accepted);
    }

    [Fact]
    public async Task OversizedContentIsRejected()
    {
        var (service, _) = Build();

        var result = await service.IngestAsync(
            PipelineTestHarness.Envelope(new string('x', 20_001), sourceIdentifier: "1"),
            CancellationToken.None);

        Assert.False(result.Accepted);
    }

    private static (ObservationIngestionService Service, ChannelObservationBuffer Buffer) Build()
    {
        var buffer = new ChannelObservationBuffer(Options.Create(new PipelineOptions { QueueCapacity = 16 }));
        var diagnostics = new PipelineDiagnostics(new TestMeterFactory());
        return (new ObservationIngestionService(buffer, diagnostics, NullLogger<ObservationIngestionService>.Instance), buffer);
    }
}
