using System.Text.Json;
using System.Text.Json.Serialization;
using Geopolitics.Application.Abstractions;
using Geopolitics.Application.Contracts;
using Geopolitics.Domain;

namespace Geopolitics.IntegrationTests;

/// <summary>
/// A finite, deliberately-shaped observation stream, used to drive the composed host through cases
/// that real reporting cannot be relied upon to contain on any given day: a byte-identical
/// redelivery, two outlets describing one event, a place name no gazetteer resolves, and a provider
/// that supplies its own coordinates.
/// <para>
/// This lives in the test project, and that is the whole point of it being here rather than in
/// <c>Geopolitics.Infrastructure</c>. It used to be a registered <see cref="IEventSource"/> in the
/// application, which meant the shipped product could emit fabricated records into its own read
/// model — and did: the published snapshot of 2026-09-15 carried eleven of them. The application no
/// longer contains anything that can produce a synthetic observation, so the only way this data
/// reaches a pipeline is a test asking for it explicitly. See ADR 040.
/// </para>
/// <para>
/// Every record it emits is still marked <see cref="ObservationProvenance.Recorded"/>. The labelling
/// is not redundant just because the producer is confined to tests: a fixture that presented itself
/// as live reporting would let an assertion pass for the wrong reason.
/// </para>
/// </summary>
public sealed class ScriptedEventSource(TimeProvider timeProvider) : IEventSource
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
        ReadCommentHandling = JsonCommentHandling.Skip,
    };

    /// <summary>How many records the fixture holds, so a test can wait for the stream to drain.</summary>
    public static int RecordCount => Load().Observations.Count;

    public string Name => "scripted";

    /// <summary>
    /// Emits the whole fixture and stops. No delays and no looping: the recorded inter-arrival gaps
    /// existed to make a live demo look alive, and a test waiting on wall-clock time is a test that
    /// is slow when it passes and flaky when it does not.
    /// </summary>
    public async IAsyncEnumerable<ObservationEnvelope> ReadAsync(
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();

        foreach (var record in Load().Observations)
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return ToEnvelope(record, now);
        }

        await Task.CompletedTask;
    }

    private static ObservationEnvelope ToEnvelope(ScriptedRecord record, DateTimeOffset now) => new()
    {
        SourceName = record.SourceName,
        Kind = record.Kind,
        Content = record.Content,
        SourceIdentifier = record.SourceIdentifier,
        Title = record.Title,
        OccurredAt = now.AddMinutes(record.OccurredAtOffsetMinutes),
        DeclaredLocationName = record.DeclaredLocationName,
        DeclaredLatitude = record.DeclaredLatitude,
        DeclaredLongitude = record.DeclaredLongitude,
        DeclaredCountryCode = record.DeclaredCountryCode,
        DeclaredEventType = record.DeclaredEventType,
        DeclaredSeverity = record.DeclaredSeverity,

        // Non-negotiable, and kept from the source this replaced: fabricated records are labelled as
        // such everywhere they travel, including inside a test.
        Provenance = ObservationProvenance.Recorded,
    };

    private static ScriptedScript Load()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "pipeline", "scripted-stream.json");

        var script = JsonSerializer.Deserialize<ScriptedScript>(File.ReadAllText(path), SerializerOptions)
            ?? throw new InvalidOperationException($"Scripted stream fixture at '{path}' could not be deserialised.");

        return script.Observations.Count == 0
            ? throw new InvalidOperationException($"Scripted stream fixture at '{path}' contains no observations.")
            : script;
    }

    private sealed record ScriptedScript
    {
        public string Scenario { get; init; } = "unnamed";

        public IReadOnlyList<ScriptedRecord> Observations { get; init; } = [];
    }

    private sealed record ScriptedRecord
    {
        public int DelayMilliseconds { get; init; }

        public string SourceName { get; init; } = "scripted:unknown";

        public ObservationKind Kind { get; init; } = ObservationKind.Replay;

        public string? SourceIdentifier { get; init; }

        public string? Title { get; init; }

        public string Content { get; init; } = string.Empty;

        public string? DeclaredLocationName { get; init; }

        public double? DeclaredLatitude { get; init; }

        public double? DeclaredLongitude { get; init; }

        public string? DeclaredCountryCode { get; init; }

        public EventType? DeclaredEventType { get; init; }

        public Severity? DeclaredSeverity { get; init; }

        /// <summary>Event time relative to the run, so placement and recency assertions stay stable.</summary>
        public int OccurredAtOffsetMinutes { get; init; }
    }
}
