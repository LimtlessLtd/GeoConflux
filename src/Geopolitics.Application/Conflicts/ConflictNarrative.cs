using System.Text.Json;
using System.Text.Json.Serialization;

namespace Geopolitics.Application.Conflicts;

/// <param name="Conflict">Register key.</param>
/// <param name="Name">The conflict as the coding names it.</param>
/// <param name="Window">The window this characterises.</param>
/// <param name="Text">
/// The summary, or the refusal. Always a sentence a reader can act on: "too little to characterise"
/// is information, and an empty panel is not.
/// </param>
/// <param name="IsModelWritten">
/// Whether a model wrote this. False for every refusal, which is the distinction that matters most on
/// the page — a refusal is a fact about the evidence and must never be mistaken for an assessment.
/// </param>
/// <param name="Reports">How many reports it was written from. Shown beside it, never implied.</param>
/// <param name="Sources">How many distinct sources produced them.</param>
/// <param name="Confidence">The model's own certainty, when a model wrote it.</param>
/// <param name="Provider">Which provider and model produced it, for traceability.</param>
/// <param name="PromptVersion">Version of the instruction that produced it.</param>
public sealed record ConflictNarrative(
    string Conflict,
    string Name,
    string Window,
    string Text,
    bool IsModelWritten,
    int Reports,
    int Sources,
    double? Confidence,
    string? Provider,
    string? PromptVersion)
{
    /// <summary>A refusal, written by this system rather than by a model, and labelled as such.</summary>
    public static ConflictNarrative Declined(
        string key,
        string name,
        string window,
        string text,
        int reports,
        int sources) =>
        new(key, name, window, text, IsModelWritten: false, reports, sources, null, null, null);
}

/// <param name="Title">Headline, when the report had one.</param>
/// <param name="Summary">The report's summary. An excerpt, never the article.</param>
/// <param name="SourceName">Who reported it.</param>
/// <param name="PlaceName">Where it was placed, when it was placed.</param>
/// <param name="OccurredAt">When the event happened, as the source states it.</param>
public sealed record ConflictEvidence(
    string? Title,
    string? Summary,
    string SourceName,
    string? PlaceName,
    DateTimeOffset OccurredAt);

/// <summary>
/// The contract for a per-conflict summary.
/// <para>
/// Narrow on purpose. A summary and a confidence, and no field in which a model could assert a
/// casualty figure, a front line, a unit position, or a projection — the things this system refuses
/// everywhere else and which a free-text field would quietly re-admit through the one place a model
/// is asked to write prose.
/// </para>
/// </summary>
public static class ConflictNarrativeContract
{
    public const int SchemaVersion = 1;

    public const int MaxSummaryLength = 700;

    public static JsonSerializerOptions SerializerOptions { get; } = new(JsonSerializerDefaults.Web)
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        NumberHandling = JsonNumberHandling.AllowReadingFromString,
    };

    public static JsonElement ResponseSchema { get; } = JsonDocument.Parse("""
        {
          "type": "object",
          "additionalProperties": false,
          "required": ["schemaVersion", "summary", "confidence"],
          "properties": {
            "schemaVersion": { "type": "integer" },
            "summary": { "type": "string", "description": "What the reports below describe, in at most four sentences. Only what they say." },
            "confidence": { "type": "number", "description": "0-1 certainty that these reports support the summary." }
          }
        }
        """).RootElement.Clone();
}

/// <summary>Raw shape returned by the model. Untrusted until validated.</summary>
public sealed record ConflictNarrativePayload
{
    public int SchemaVersion { get; init; }

    public string? Summary { get; init; }

    public double Confidence { get; init; }
}
