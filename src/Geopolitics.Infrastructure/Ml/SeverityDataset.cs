using System.Text.Json;
using System.Text.Json.Serialization;
using Geopolitics.Application.Abstractions;
using Geopolitics.Domain;

namespace Geopolitics.Infrastructure.Ml;

/// <param name="Id">Stable identifier, so a disagreement in the evaluation report can be looked up.</param>
/// <param name="Features">Exactly what the model sees, built through the same factory the pipeline uses.</param>
/// <param name="Expected">The author-assigned label.</param>
public sealed record LabelledSeverityCase(string Id, string Title, SeverityFeatures Features, Severity Expected);

/// <summary>
/// The labelled corpus behind the severity model, with its split fixed in the file.
/// <para>
/// Embedded rather than read from disk, for the same reason the replay stream is: the application
/// must work from a published single file with no data directory alongside it. It is also the reason
/// the corpus lives in this project rather than under <c>tests/</c> — it is production data that
/// trains a shipped model, not a test fixture, even though the evaluation is the thing that reads it
/// most closely.
/// </para>
/// <para>
/// The split is read, never computed. Drawing it at load time would make every figure in the
/// evaluation report a different measurement on each run, so a genuine regression would be
/// indistinguishable from a reshuffle.
/// </para>
/// </summary>
public static class SeverityDataset
{
    private const string ResourceName = "Geopolitics.Infrastructure.Ml.Data.severity-dataset.json";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private static readonly Lazy<LoadedDataset> Loaded = new(Load, LazyThreadSafetyMode.ExecutionAndPublication);

    /// <summary>Dataset version, which forms part of the trained model's version string.</summary>
    public static int Version => Loaded.Value.Version;

    /// <summary>The provenance statement carried in the file, surfaced wherever results are reported.</summary>
    public static string Notice => Loaded.Value.Notice;

    /// <summary>Cases the model may learn from.</summary>
    public static IReadOnlyList<LabelledSeverityCase> Training => Loaded.Value.Training;

    /// <summary>
    /// Cases held out of training. Nothing in this list may influence the model; the evaluation
    /// asserts the two sets are disjoint, because a leak would inflate every reported figure while
    /// leaving the report looking entirely reasonable.
    /// </summary>
    public static IReadOnlyList<LabelledSeverityCase> Holdout => Loaded.Value.Holdout;

    private static LoadedDataset Load()
    {
        using var stream = typeof(SeverityDataset).Assembly.GetManifestResourceStream(ResourceName)
            ?? throw new InvalidOperationException($"The embedded severity dataset '{ResourceName}' is missing.");

        var file = JsonSerializer.Deserialize<DatasetFile>(stream, Json)
            ?? throw new InvalidOperationException("The embedded severity dataset could not be read.");

        var training = new List<LabelledSeverityCase>();
        var holdout = new List<LabelledSeverityCase>();

        foreach (var record in file.Cases)
        {
            var labelled = new LabelledSeverityCase(
                record.Id,
                record.Title,
                SeverityFeatures.From(
                    record.Title,
                    record.Content,
                    ParseEventType(record.EventType),
                    record.SourceCount,
                    record.SourceConfidence,
                    record.EntityCount,
                    record.HasLocation),
                ParseSeverity(record.ExpectedSeverity));

            (string.Equals(record.Split, "test", StringComparison.OrdinalIgnoreCase) ? holdout : training)
                .Add(labelled);
        }

        return new LoadedDataset(file.DatasetVersion, file.Notice, training, holdout);
    }

    /// <summary>
    /// Wire labels are SCREAMING_SNAKE, matching the enrichment contract; the enum is PascalCase.
    /// An unrecognised value is an error rather than a silent fallback, because a typo that mapped
    /// to <see cref="EventType.Other"/> would quietly degrade the training set instead of failing.
    /// </summary>
    private static EventType ParseEventType(string value) =>
        Enum.TryParse<EventType>(value.Replace("_", string.Empty, StringComparison.Ordinal), ignoreCase: true, out var parsed)
            ? parsed
            : throw new InvalidOperationException($"The severity dataset contains an unknown event type '{value}'.");

    private static Severity ParseSeverity(string value) =>
        Enum.TryParse<Severity>(value, ignoreCase: true, out var parsed) && parsed != Severity.Unknown
            ? parsed
            : throw new InvalidOperationException($"The severity dataset contains an unusable label '{value}'.");

    private sealed record LoadedDataset(
        int Version,
        string Notice,
        IReadOnlyList<LabelledSeverityCase> Training,
        IReadOnlyList<LabelledSeverityCase> Holdout);

    private sealed record DatasetFile
    {
        public int DatasetVersion { get; init; }

        public string Notice { get; init; } = string.Empty;

        [JsonPropertyName("cases")]
        public IReadOnlyList<CaseRecord> Cases { get; init; } = [];
    }

    private sealed record CaseRecord
    {
        public string Id { get; init; } = string.Empty;

        public string Split { get; init; } = "train";

        public string Title { get; init; } = string.Empty;

        public string Content { get; init; } = string.Empty;

        public string EventType { get; init; } = "OTHER";

        public int SourceCount { get; init; } = 1;

        public double SourceConfidence { get; init; }

        public int EntityCount { get; init; }

        public bool HasLocation { get; init; }

        public string ExpectedSeverity { get; init; } = string.Empty;
    }
}
