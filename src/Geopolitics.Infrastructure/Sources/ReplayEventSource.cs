using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using Geopolitics.Application.Abstractions;
using Geopolitics.Application.Contracts;
using Geopolitics.Domain;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Geopolitics.Infrastructure.Sources;

/// <summary>
/// Emits a recorded, synthetic observation stream, per ADR 007.
/// <para>
/// Every envelope it produces is flagged as demo data and carries a <c>replay:</c> source prefix, so
/// nothing it emits can be mistaken for live reporting anywhere downstream. The recorded delays make
/// the demo behave like a real stream — items arrive over time and the dashboard updates as they do —
/// while the ordering and content stay identical on every run, which is what makes the pipeline's
/// deduplication and correlation behaviour reproducible in tests.
/// </para>
/// </summary>
public sealed partial class ReplayEventSource(
    TimeProvider timeProvider,
    IOptions<ReplayOptions> options,
    ILogger<ReplayEventSource> logger) : IEventSource
{
    private const string ResourceName = "Geopolitics.Infrastructure.Sources.Data.replay-observations.json";

    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
        ReadCommentHandling = JsonCommentHandling.Skip,
    };

    private readonly ReplayOptions options = options.Value;

    public string Name => "replay";

    public async IAsyncEnumerable<ObservationEnvelope> ReadAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        if (!options.Enabled)
        {
            LogReplayDisabled(logger);
            yield break;
        }

        var script = await LoadScriptAsync(cancellationToken);
        LogReplayStarting(logger, script.Observations.Count, script.Scenario);

        var pass = 0;

        do
        {
            foreach (var record in script.Observations)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var delay = TimeSpan.FromMilliseconds(Math.Max(0, record.DelayMilliseconds) * options.SpeedFactor);

                if (delay > TimeSpan.Zero)
                {
                    await Task.Delay(delay, timeProvider, cancellationToken);
                }

                yield return ToEnvelope(record, timeProvider.GetUtcNow(), pass);
            }

            pass++;

            if (options.Loop)
            {
                LogReplayLooping(logger, pass);
                await Task.Delay(options.LoopPause, timeProvider, cancellationToken);
            }
        }
        while (options.Loop && !cancellationToken.IsCancellationRequested);

        LogReplayCompleted(logger, script.Observations.Count);
    }

    /// <param name="pass">
    /// Replay iteration. On a looping replay the identifier is suffixed so that a second pass
    /// produces genuinely new observations rather than a stream the deduplicator silently discards.
    /// </param>
    private static ObservationEnvelope ToEnvelope(ReplayRecord record, DateTimeOffset now, int pass) => new()
    {
        SourceName = record.SourceName,
        Kind = record.Kind,
        Content = record.Content,
        SourceIdentifier = pass == 0 ? record.SourceIdentifier : $"{record.SourceIdentifier}#{pass}",
        Title = record.Title,
        OccurredAt = now.AddMinutes(record.OccurredAtOffsetMinutes),
        DeclaredLocationName = record.DeclaredLocationName,
        DeclaredLatitude = record.DeclaredLatitude,
        DeclaredLongitude = record.DeclaredLongitude,
        DeclaredCountryCode = record.DeclaredCountryCode,
        DeclaredEventType = record.DeclaredEventType,
        DeclaredSeverity = record.DeclaredSeverity,

        // Non-negotiable: replay output is demo data and is labelled as such everywhere it travels.
        Provenance = ObservationProvenance.Recorded,
    };

    private static async Task<ReplayScript> LoadScriptAsync(CancellationToken cancellationToken)
    {
        await using var stream = typeof(ReplayEventSource).Assembly.GetManifestResourceStream(ResourceName)
            ?? throw new InvalidOperationException(
                $"Embedded replay data '{ResourceName}' is missing from {Assembly.GetExecutingAssembly().GetName().Name}.");

        var script = await JsonSerializer.DeserializeAsync<ReplayScript>(stream, SerializerOptions, cancellationToken)
            ?? throw new InvalidOperationException("Embedded replay data could not be deserialised.");

        return script.Observations.Count == 0
            ? throw new InvalidOperationException("Embedded replay data contains no observations.")
            : script;
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Replay source is disabled by configuration.")]
    private static partial void LogReplayDisabled(ILogger logger);

    [LoggerMessage(Level = LogLevel.Information, Message = "Replay source starting with {Count} recorded demo observations ({Scenario}). This is synthetic data, not live reporting.")]
    private static partial void LogReplayStarting(ILogger logger, int count, string scenario);

    [LoggerMessage(Level = LogLevel.Information, Message = "Replay source beginning pass {Pass}.")]
    private static partial void LogReplayLooping(ILogger logger, int pass);

    [LoggerMessage(Level = LogLevel.Information, Message = "Replay source completed after emitting {Count} demo observations.")]
    private static partial void LogReplayCompleted(ILogger logger, int count);

    private sealed record ReplayScript
    {
        public string Scenario { get; init; } = "unnamed";

        public IReadOnlyList<ReplayRecord> Observations { get; init; } = [];
    }

    private sealed record ReplayRecord
    {
        public int DelayMilliseconds { get; init; }

        public string SourceName { get; init; } = "replay:unknown";

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

        /// <summary>Event time relative to replay start, so a demo always shows recent activity.</summary>
        public int OccurredAtOffsetMinutes { get; init; }
    }
}
