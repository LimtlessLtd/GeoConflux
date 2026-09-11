using System.Text.Json;
using System.Text.Json.Serialization;
using Geopolitics.Domain;

namespace Geopolitics.Application.Enrichment;

/// <summary>
/// The versioned output contract the enrichment model is asked to satisfy.
/// <para>
/// The wire names are declared here explicitly rather than derived from the C# enum members. That is
/// the point of versioning a contract: renaming <see cref="EventType.MaritimeIncident"/> is an
/// internal refactor, and it must not silently change what a model is asked to emit or what a stored
/// inference means when it is read back months later.
/// </para>
/// </summary>
public static class EnrichmentContract
{
    /// <summary>Increment when the shape changes in a way a previously stored payload would fail.</summary>
    public const int SchemaVersion = 1;

    public const int MaxSummaryLength = 1200;
    public const int MaxLocationNameLength = 120;
    public const int MaxLocations = 5;
    public const int MaxEntities = RawObservation.MaxEntities;
    public const int MaxLanguageTagLength = 16;

    private static readonly (string Wire, EventType Value)[] EventTypeMap =
    [
        ("CONFLICT", EventType.Conflict),
        ("MARITIME_INCIDENT", EventType.MaritimeIncident),
        ("NAVAL_INCIDENT", EventType.NavalIncident),
        ("PIRACY", EventType.Piracy),
        ("TERRORISM", EventType.Terrorism),
        ("PROTEST", EventType.Protest),
        ("SANCTIONS", EventType.Sanctions),
        ("MILITARY_MOVEMENT", EventType.MilitaryMovement),
        ("CYBER_INCIDENT", EventType.CyberIncident),
        ("NATURAL_HAZARD", EventType.NaturalHazard),
        ("OTHER", EventType.Other),
    ];

    private static readonly (string Wire, Severity Value)[] SeverityMap =
    [
        ("LOW", Severity.Low),
        ("MEDIUM", Severity.Medium),
        ("HIGH", Severity.High),
        ("CRITICAL", Severity.Critical),
        ("UNKNOWN", Severity.Unknown),
    ];

    private static readonly (string Wire, EntityType Value)[] EntityTypeMap =
    [
        ("PERSON", EntityType.Person),
        ("ORGANISATION", EntityType.Organisation),
        ("STATE", EntityType.State),
        ("MILITARY_UNIT", EntityType.MilitaryUnit),
        ("VESSEL", EntityType.Vessel),
        ("PLACE", EntityType.Place),
        ("UNKNOWN", EntityType.Unknown),
    ];

    /// <summary>Allowed event-type values, used in the prompt and in validation error messages.</summary>
    public static IReadOnlyList<string> EventTypeNames { get; } = [.. EventTypeMap.Select(entry => entry.Wire)];

    public static IReadOnlyList<string> SeverityNames { get; } = [.. SeverityMap.Select(entry => entry.Wire)];

    public static IReadOnlyList<string> EntityTypeNames { get; } = [.. EntityTypeMap.Select(entry => entry.Wire)];

    public static JsonSerializerOptions SerializerOptions { get; } = new(JsonSerializerDefaults.Web)
    {
        // The contract is exact: an unmapped property means the model produced something other than
        // what was asked for, and that is worth surfacing rather than quietly discarding.
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        NumberHandling = JsonNumberHandling.AllowReadingFromString,
    };

    public static bool TryParseEventType(string? wire, out EventType value) => TryMap(EventTypeMap, wire, out value);

    public static bool TryParseSeverity(string? wire, out Severity value) => TryMap(SeverityMap, wire, out value);

    public static bool TryParseEntityType(string? wire, out EntityType value) => TryMap(EntityTypeMap, wire, out value);

    public static string ToWire(EventType value) => EventTypeMap.First(entry => entry.Value == value).Wire;

    public static string ToWire(Severity value) => SeverityMap.First(entry => entry.Value == value).Wire;

    public static string ToWire(EntityType value) => EntityTypeMap.First(entry => entry.Value == value).Wire;

    /// <summary>
    /// The JSON schema handed to providers that support constrained decoding. Every property is
    /// required and <c>additionalProperties</c> is false, which is what strict structured-output
    /// modes demand. Providers without that capability get the same shape described in the prompt
    /// and are validated identically on the way back, so the guarantee does not depend on the model.
    /// </summary>
    public static JsonElement ResponseSchema { get; } = JsonDocument.Parse(BuildSchema()).RootElement.Clone();

    private static bool TryMap<T>((string Wire, T Value)[] map, string? wire, out T value)
        where T : struct
    {
        if (!string.IsNullOrWhiteSpace(wire))
        {
            var normalised = wire.Trim().Replace(' ', '_').Replace('-', '_').ToUpperInvariant();

            foreach (var entry in map)
            {
                if (string.Equals(entry.Wire, normalised, StringComparison.Ordinal))
                {
                    value = entry.Value;
                    return true;
                }
            }
        }

        value = default;
        return false;
    }

    /// <summary>
    /// Written as one literal document with the enum lists substituted in, rather than assembled
    /// from concatenated fragments. A schema built by concatenation is one missing brace away from
    /// a type initialiser that throws at first use, and the literal form makes the structure
    /// reviewable at a glance. <c>EnrichmentContractTests</c> parses the result so a malformed
    /// schema fails the build rather than the pipeline.
    /// </summary>
    private static string BuildSchema() =>
        """
        {
          "type": "object",
          "additionalProperties": false,
          "required": ["schemaVersion", "language", "summary", "eventType", "severity", "confidence", "locations", "entities", "severityRationale"],
          "properties": {
            "schemaVersion": { "type": "integer" },
            "language": { "type": "string", "description": "BCP-47 tag of the ORIGINAL text, for example ar, ru, en." },
            "summary": { "type": "string", "description": "Short factual summary in English." },
            "eventType": { "type": "string", "enum": [__EVENT_TYPES__] },
            "severity": { "type": "string", "enum": [__SEVERITIES__] },
            "confidence": { "type": "number", "description": "0-1 certainty in the classification." },
            "severityRationale": { "type": "string", "description": "One sentence justifying the severity. Not a reasoning transcript." },
            "locations": {
              "type": "array",
              "items": {
                "type": "object",
                "additionalProperties": false,
                "required": ["name", "country"],
                "properties": {
                  "name": { "type": "string", "description": "Place NAME only. Never coordinates." },
                  "country": { "type": "string" }
                }
              }
            },
            "entities": {
              "type": "array",
              "items": {
                "type": "object",
                "additionalProperties": false,
                "required": ["name", "type"],
                "properties": {
                  "name": { "type": "string" },
                  "type": { "type": "string", "enum": [__ENTITY_TYPES__] }
                }
              }
            }
          }
        }
        """
        .Replace("__EVENT_TYPES__", Quote(EventTypeNames), StringComparison.Ordinal)
        .Replace("__SEVERITIES__", Quote(SeverityNames), StringComparison.Ordinal)
        .Replace("__ENTITY_TYPES__", Quote(EntityTypeNames), StringComparison.Ordinal);

    private static string Quote(IEnumerable<string> values) =>
        string.Join(", ", values.Select(value => "\"" + value + "\""));
}

/// <summary>Raw shape returned by the model. Untrusted until <see cref="EnrichmentPayloadValidator"/> passes it.</summary>
public sealed record AiEnrichmentPayload
{
    public int SchemaVersion { get; init; }

    public string? Language { get; init; }

    public string? Summary { get; init; }

    public string? EventType { get; init; }

    public string? Severity { get; init; }

    public double Confidence { get; init; }

    public string? SeverityRationale { get; init; }

    public IReadOnlyList<AiLocationClaim>? Locations { get; init; }

    public IReadOnlyList<AiEntityClaim>? Entities { get; init; }
}

/// <summary>
/// A place the model believes the text refers to. It carries a name and a country and nothing else:
/// there is deliberately no latitude or longitude field, so the contract itself makes it impossible
/// for a model to supply coordinates (ADR 005).
/// </summary>
public sealed record AiLocationClaim(string? Name, string? Country);

public sealed record AiEntityClaim(string? Name, string? Type);
