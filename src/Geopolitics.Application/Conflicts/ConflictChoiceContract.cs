using System.Text.Json;
using System.Text.Json.Serialization;

namespace Geopolitics.Application.Conflicts;

/// <summary>
/// The contract a model answers when it is asked which conflict a report belongs to.
/// <para>
/// The important property is what is <em>absent</em>. There is no field for a conflict key the model
/// invents: the allowed values are enumerated per request from the register, so the schema itself
/// makes it impossible for a model to name a category that does not exist. That is the same technique
/// <see cref="Enrichment.AiLocationClaim"/> uses to make it impossible for a model to supply a
/// coordinate, and it is here for the same reason — a rule enforced by the shape of the request
/// cannot be forgotten by a future caller.
/// </para>
/// <para>
/// A model that thinks none of the offered conflicts fits says so, and may name what it thinks this
/// is instead. That is a claim and is handled as one; it does not enter the register.
/// </para>
/// </summary>
public static class ConflictChoiceContract
{
    public const int SchemaVersion = 1;

    /// <summary>The reserved answer meaning "none of these". Not a key, so it can never collide with one.</summary>
    public const string None = "NONE";

    public const int MaxRationaleLength = 300;

    public const int MaxProposedNameLength = 120;

    /// <summary>
    /// How many conflicts are put to the model at once. The deterministic pass rarely leaves more
    /// than a handful, and a list longer than this is not a question a model can usefully answer — it
    /// is a sign that the report identified nothing, which is already the answer.
    /// </summary>
    public const int MaxOptions = 8;

    public static JsonSerializerOptions SerializerOptions { get; } = new(JsonSerializerDefaults.Web)
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        NumberHandling = JsonNumberHandling.AllowReadingFromString,
    };

    /// <summary>
    /// The response schema for one request, with that request's conflict keys as the only permitted
    /// answers. Built per call because the options are, which is the whole point of it.
    /// </summary>
    public static JsonElement ResponseSchema(IReadOnlyList<string> keys)
    {
        ArgumentNullException.ThrowIfNull(keys);

        var allowed = string.Join(", ", keys.Append(None).Select(key => "\"" + key.Replace("\"", "\\\"", StringComparison.Ordinal) + "\""));

        var schema = """
        {
          "type": "object",
          "additionalProperties": false,
          "required": ["schemaVersion", "conflictKey", "confidence", "rationale"],
          "properties": {
            "schemaVersion": { "type": "integer" },
            "conflictKey": { "type": "string", "enum": [__KEYS__], "description": "The conflict this report belongs to, or NONE." },
            "confidence": { "type": "number", "description": "0-1 certainty in the choice." },
            "rationale": { "type": "string", "description": "One sentence. What in the report puts it there. Not a reasoning transcript." },
            "proposedName": { "type": "string", "description": "Only when conflictKey is NONE and the report describes organised violence no listed conflict covers. Name it as reporting names it." }
          }
        }
        """;

        return JsonDocument.Parse(schema.Replace("__KEYS__", allowed, StringComparison.Ordinal)).RootElement.Clone();
    }
}

/// <summary>Raw shape returned by the model. Untrusted until it has been validated against the offer.</summary>
public sealed record ConflictChoicePayload
{
    public int SchemaVersion { get; init; }

    public string? ConflictKey { get; init; }

    public double Confidence { get; init; }

    public string? Rationale { get; init; }

    public string? ProposedName { get; init; }
}

/// <param name="ConflictKey">A key from the offer, or <see langword="null"/> when the model chose none.</param>
/// <param name="Confidence">The model's certainty, 0-1. Recorded whether or not the choice is adopted.</param>
/// <param name="Rationale">One sentence on what in the report puts it there.</param>
/// <param name="ProposedName">What the model thinks this is, when it thinks nothing offered covers it.</param>
public sealed record ConflictChoice(
    string? ConflictKey,
    double Confidence,
    string? Rationale,
    string? ProposedName);
