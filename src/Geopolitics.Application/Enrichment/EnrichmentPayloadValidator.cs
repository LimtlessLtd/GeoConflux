using System.Text;
using System.Text.Json;
using Geopolitics.Domain;

namespace Geopolitics.Application.Enrichment;

/// <param name="Summary">English summary, sanitised and length-bounded.</param>
/// <param name="EventType">Category, mapped from a known wire value.</param>
/// <param name="Severity">Assessed severity, mapped from a known wire value.</param>
/// <param name="Confidence">Model-reported certainty, guaranteed to be within 0-1.</param>
/// <param name="Language">BCP-47 tag of the original text, when the model identified one.</param>
/// <param name="SeverityRationale">One-sentence justification, sanitised.</param>
/// <param name="LocationName">Primary place NAME. Never accompanied by coordinates.</param>
/// <param name="Entities">Named actors, deduplicated and capped.</param>
public sealed record ValidatedEnrichment(
    string Summary,
    EventType EventType,
    Severity Severity,
    double Confidence,
    string? Language,
    string? SeverityRationale,
    string? LocationName,
    IReadOnlyList<ExtractedEntity> Entities);

/// <param name="Value">The accepted result, or <see langword="null"/> when validation failed.</param>
/// <param name="Errors">Every problem found, phrased so it can be fed back to the model as a repair instruction.</param>
public sealed record EnrichmentValidation(ValidatedEnrichment? Value, IReadOnlyList<string> Errors)
{
    public bool IsValid => Value is not null;

    public string ErrorSummary => string.Join(" ", Errors);
}

/// <summary>
/// The trust boundary for model output.
/// <para>
/// Everything arriving here is untrusted text that happens to look like JSON. The validator reports
/// <em>all</em> problems rather than failing on the first, because the collected list is what gets
/// handed back to the model as a repair instruction, and a one-error-at-a-time loop would burn a
/// round trip per mistake.
/// </para>
/// </summary>
public static class EnrichmentPayloadValidator
{
    public static EnrichmentValidation Validate(string? responseText)
    {
        if (string.IsNullOrWhiteSpace(responseText))
        {
            return Invalid("The response was empty.");
        }

        AiEnrichmentPayload? payload;

        try
        {
            payload = JsonSerializer.Deserialize<AiEnrichmentPayload>(
                StripCodeFence(responseText),
                EnrichmentContract.SerializerOptions);
        }
        catch (JsonException exception)
        {
            // The provider's own message names the offending path, which is exactly what a repair
            // attempt needs. It is model output, not a secret, so it is safe to echo back.
            return Invalid($"The response was not valid JSON matching the schema: {exception.Message}");
        }

        return payload is null
            ? Invalid("The response deserialised to nothing.")
            : Validate(payload);
    }

    public static EnrichmentValidation Validate(AiEnrichmentPayload payload)
    {
        ArgumentNullException.ThrowIfNull(payload);

        var errors = new List<string>();

        if (payload.SchemaVersion != EnrichmentContract.SchemaVersion)
        {
            errors.Add($"schemaVersion must be {EnrichmentContract.SchemaVersion} but was {payload.SchemaVersion}.");
        }

        var summary = Sanitise(payload.Summary, EnrichmentContract.MaxSummaryLength);

        if (string.IsNullOrWhiteSpace(summary))
        {
            errors.Add("summary is required and must be non-empty.");
        }

        if (!EnrichmentContract.TryParseEventType(payload.EventType, out var eventType))
        {
            errors.Add($"eventType '{Describe(payload.EventType)}' is not one of: {string.Join(", ", EnrichmentContract.EventTypeNames)}.");
        }

        if (!EnrichmentContract.TryParseSeverity(payload.Severity, out var severity))
        {
            errors.Add($"severity '{Describe(payload.Severity)}' is not one of: {string.Join(", ", EnrichmentContract.SeverityNames)}.");
        }

        // NaN and infinity survive JSON parsing in some provider dialects and would poison every
        // downstream average, so they are rejected rather than clamped.
        if (double.IsNaN(payload.Confidence) || double.IsInfinity(payload.Confidence) || payload.Confidence is < 0 or > 1)
        {
            errors.Add($"confidence must be a number between 0 and 1 but was {payload.Confidence}.");
        }

        if (payload.Locations is { Count: > EnrichmentContract.MaxLocations })
        {
            errors.Add($"locations may contain at most {EnrichmentContract.MaxLocations} entries but contained {payload.Locations.Count}.");
        }

        if (payload.Entities is { Count: > EnrichmentContract.MaxEntities })
        {
            errors.Add($"entities may contain at most {EnrichmentContract.MaxEntities} entries but contained {payload.Entities.Count}.");
        }

        if (errors.Count > 0)
        {
            return new EnrichmentValidation(null, errors);
        }

        return new EnrichmentValidation(
            new ValidatedEnrichment(
                summary!,
                eventType,
                severity,
                payload.Confidence,
                SanitiseLanguageTag(payload.Language),
                Sanitise(payload.SeverityRationale, AiInference.MaxRationaleLength),
                SelectLocationName(payload.Locations),
                ReadEntities(payload.Entities)),
            []);
    }

    /// <summary>
    /// Takes the first usable place name. Multiple names are common when an article mentions several
    /// places; the first is the model's primary claim, and the resolver decides whether it means
    /// anything. A name it cannot resolve simply leaves the observation unplaced.
    /// </summary>
    private static string? SelectLocationName(IReadOnlyList<AiLocationClaim>? locations)
    {
        if (locations is null)
        {
            return null;
        }

        foreach (var location in locations)
        {
            var name = Sanitise(location?.Name, EnrichmentContract.MaxLocationNameLength);

            if (!string.IsNullOrWhiteSpace(name))
            {
                return name;
            }
        }

        return null;
    }

    /// <summary>
    /// Reads entity claims, skipping unusable ones instead of failing the whole payload. A malformed
    /// entity costs a little display detail; a rejected payload costs the classification as well,
    /// and that trade is not worth making for an optional field.
    /// </summary>
    private static List<ExtractedEntity> ReadEntities(IReadOnlyList<AiEntityClaim>? claims)
    {
        if (claims is null || claims.Count == 0)
        {
            return [];
        }

        var results = new List<ExtractedEntity>(Math.Min(claims.Count, EnrichmentContract.MaxEntities));
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var claim in claims)
        {
            if (results.Count == EnrichmentContract.MaxEntities)
            {
                break;
            }

            var name = Sanitise(claim?.Name, ExtractedEntity.MaxNameLength);

            if (string.IsNullOrWhiteSpace(name) || !seen.Add(name))
            {
                continue;
            }

            // An unrecognised entity type is a labelling miss, not a corrupt record: keep the name
            // and mark the type unknown rather than discarding an actor the text really mentions.
            var recognised = EnrichmentContract.TryParseEntityType(claim?.Type, out var type);
            results.Add(new ExtractedEntity(name, recognised ? type : EntityType.Unknown));
        }

        return results;
    }

    /// <summary>
    /// Models frequently wrap JSON in a markdown fence. That is a presentation artefact rather than
    /// a semantic error, so it is stripped here instead of costing a repair round trip.
    /// </summary>
    private static string StripCodeFence(string text)
    {
        var trimmed = text.Trim();

        if (!trimmed.StartsWith("```", StringComparison.Ordinal))
        {
            return trimmed;
        }

        var firstNewline = trimmed.IndexOf('\n');

        if (firstNewline < 0)
        {
            return trimmed;
        }

        var body = trimmed[(firstNewline + 1)..];
        var closing = body.LastIndexOf("```", StringComparison.Ordinal);

        return (closing < 0 ? body : body[..closing]).Trim();
    }

    /// <summary>
    /// Removes control characters and collapses whitespace. Model output is rendered in a browser
    /// and written to structured logs; escape sequences and newline injection in a field that is
    /// supposed to hold one sentence have no legitimate use.
    /// </summary>
    private static string? Sanitise(string? value, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var builder = new StringBuilder(Math.Min(value.Length, maxLength));
        var pendingSpace = false;

        foreach (var character in value)
        {
            if (char.IsControl(character) || char.IsWhiteSpace(character))
            {
                pendingSpace = builder.Length > 0;
                continue;
            }

            if (pendingSpace)
            {
                builder.Append(' ');
                pendingSpace = false;
            }

            if (builder.Length == maxLength)
            {
                break;
            }

            builder.Append(character);
        }

        var result = builder.ToString().Trim();
        return result.Length == 0 ? null : result;
    }

    /// <summary>Accepts only letters, digits, and hyphens, which is all a BCP-47 tag needs.</summary>
    private static string? SanitiseLanguageTag(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var builder = new StringBuilder(EnrichmentContract.MaxLanguageTagLength);

        foreach (var character in value.Trim())
        {
            if (builder.Length == EnrichmentContract.MaxLanguageTagLength)
            {
                break;
            }

            if (char.IsAsciiLetterOrDigit(character) || character == '-')
            {
                builder.Append(char.ToLowerInvariant(character));
            }
        }

        return builder.Length == 0 ? null : builder.ToString();
    }

    private static string Describe(string? value) =>
        string.IsNullOrWhiteSpace(value) ? "(missing)" : Sanitise(value, 40) ?? "(missing)";

    private static EnrichmentValidation Invalid(string error) => new(null, [error]);
}
