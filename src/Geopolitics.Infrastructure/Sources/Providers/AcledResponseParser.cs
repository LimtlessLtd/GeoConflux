using System.Globalization;
using System.Text.Json;
using Geopolitics.Domain;

namespace Geopolitics.Infrastructure.Sources.Providers;

/// <param name="Identifier">ACLED's own event identifier, which is stable across their revisions.</param>
/// <param name="EventType">The category ACLED assigned, mapped onto this system's taxonomy.</param>
/// <param name="Latitude">Coordinates from ACLED's own coded record, not inferred from prose.</param>
/// <param name="Fatalities">Reported fatalities, which drive the severity mapping.</param>
public sealed record AcledEvent(
    string Identifier,
    string Headline,
    string Notes,
    EventType EventType,
    Severity Severity,
    double? Latitude,
    double? Longitude,
    string? CountryCode,
    string? LocationName,
    DateTimeOffset? OccurredAt,
    int Fatalities);

/// <summary>
/// Reads the JSON returned by the ACLED read API.
/// <para>
/// Every field is treated as optional and every type is checked, because this parser runs against a
/// third-party contract this repository cannot pin. ACLED returns numbers as JSON strings in some
/// fields and as numbers in others, and has changed which is which between API versions, so values
/// are read tolerantly rather than bound to a fixed shape. A row that cannot be made sense of is
/// skipped; a response that is not an ACLED response at all is an error.
/// </para>
/// </summary>
public static class AcledResponseParser
{
    /// <summary>
    /// Parses an ACLED read response.
    /// </summary>
    /// <exception cref="FormatException">The payload is not JSON, or is not an ACLED response.</exception>
    public static IReadOnlyList<AcledEvent> Parse(string document)
    {
        if (string.IsNullOrWhiteSpace(document))
        {
            throw new FormatException("The ACLED response was empty.");
        }

        JsonDocument parsed;

        try
        {
            parsed = JsonDocument.Parse(document);
        }
        catch (JsonException exception)
        {
            throw new FormatException($"The ACLED response was not valid JSON: {exception.Message}", exception);
        }

        using (parsed)
        {
            var root = parsed.RootElement;

            if (root.ValueKind != JsonValueKind.Object)
            {
                throw new FormatException("The ACLED response was not a JSON object.");
            }

            // An error reply carries success:false and a message; surfacing that text is far more
            // useful to an operator than reporting zero events.
            if (root.TryGetProperty("success", out var success)
                && success.ValueKind == JsonValueKind.False)
            {
                throw new FormatException($"ACLED rejected the request: {ErrorMessage(root)}");
            }

            if (!root.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
            {
                throw new FormatException("The ACLED response contained no data array.");
            }

            var events = new List<AcledEvent>(data.GetArrayLength());

            foreach (var element in data.EnumerateArray())
            {
                if (element.ValueKind == JsonValueKind.Object && ReadEvent(element) is { } record)
                {
                    events.Add(record);
                }
            }

            return events;
        }
    }

    private static AcledEvent? ReadEvent(JsonElement element)
    {
        var identifier = Text(element, "event_id_cnty") ?? Text(element, "data_id");

        if (string.IsNullOrWhiteSpace(identifier))
        {
            return null;
        }

        var eventType = Text(element, "event_type") ?? string.Empty;
        var subEventType = Text(element, "sub_event_type") ?? string.Empty;
        var notes = Text(element, "notes") ?? string.Empty;
        var location = Text(element, "location");
        var country = Text(element, "country");
        var fatalities = (int)Math.Clamp(Number(element, "fatalities") ?? 0, 0, int.MaxValue);

        // ACLED records are coded summaries, not articles. When the notes are absent the coded
        // fields are all there is, and they still describe a real event usefully.
        var headline = string.IsNullOrWhiteSpace(subEventType) ? eventType : subEventType;
        headline = string.IsNullOrWhiteSpace(location) ? headline : $"{headline} in {location}";

        if (string.IsNullOrWhiteSpace(headline) && string.IsNullOrWhiteSpace(notes))
        {
            return null;
        }

        var latitude = Number(element, "latitude");
        var longitude = Number(element, "longitude");

        // A coordinate pair is only usable whole. Half a pair from a provider is a data fault, and
        // passing it on would place the event on the equator or the prime meridian.
        if (latitude is null || longitude is null || latitude is < -90 or > 90 || longitude is < -180 or > 180)
        {
            latitude = null;
            longitude = null;
        }

        return new AcledEvent(
            identifier.Trim(),
            string.IsNullOrWhiteSpace(headline) ? "Recorded conflict event" : headline,
            string.IsNullOrWhiteSpace(notes) ? headline : notes,
            MapEventType(eventType, subEventType),
            MapSeverity(fatalities, eventType),
            latitude,
            longitude,
            NormaliseCountryCode(Text(element, "iso3")),
            string.IsNullOrWhiteSpace(location) ? country : location,
            ParseDate(Text(element, "event_date")),
            fatalities);
    }

    /// <summary>
    /// Maps ACLED's categories onto this system's taxonomy. Unmatched categories become
    /// <see cref="EventType.Other"/> rather than being forced into the nearest-looking bucket, so a
    /// category this mapping has not seen is visibly unclassified instead of quietly miscoded.
    /// </summary>
    private static EventType MapEventType(string eventType, string subEventType)
    {
        var combined = $"{eventType} {subEventType}".ToLowerInvariant();

        return combined switch
        {
            var value when value.Contains("protest", StringComparison.Ordinal)
                || value.Contains("riot", StringComparison.Ordinal) => EventType.Protest,
            var value when value.Contains("explosion", StringComparison.Ordinal)
                || value.Contains("remote violence", StringComparison.Ordinal) => EventType.Conflict,
            var value when value.Contains("battle", StringComparison.Ordinal) => EventType.Conflict,
            var value when value.Contains("civilian", StringComparison.Ordinal) => EventType.Terrorism,
            var value when value.Contains("strategic development", StringComparison.Ordinal) => EventType.MilitaryMovement,
            _ => EventType.Other,
        };
    }

    /// <summary>
    /// Derives severity from the fatality count ACLED coded.
    /// <para>
    /// This is a stated, auditable rule rather than a judgement: the provider supplies a number, and
    /// the number maps to a band. It is deliberately not a model output, because the provider already
    /// knows the fact that matters and inferring it from prose would be strictly worse.
    /// </para>
    /// </summary>
    private static Severity MapSeverity(int fatalities, string eventType) => fatalities switch
    {
        >= 25 => Severity.Critical,
        >= 5 => Severity.High,
        >= 1 => Severity.Medium,
        _ => eventType.Contains("Battle", StringComparison.OrdinalIgnoreCase) ? Severity.Medium : Severity.Low,
    };

    private static string ErrorMessage(JsonElement root)
    {
        if (root.TryGetProperty("error", out var error))
        {
            if (error.ValueKind == JsonValueKind.String)
            {
                return error.GetString() ?? "no reason given";
            }

            if (error.ValueKind == JsonValueKind.Array && error.GetArrayLength() > 0)
            {
                var first = error[0];

                if (first.ValueKind == JsonValueKind.Object && first.TryGetProperty("message", out var message))
                {
                    return message.GetString() ?? "no reason given";
                }
            }
        }

        return "no reason given";
    }

    private static string? Text(JsonElement element, string property)
    {
        if (!element.TryGetProperty(property, out var value))
        {
            return null;
        }

        var text = value.ValueKind switch
        {
            JsonValueKind.String => value.GetString(),
            JsonValueKind.Number => value.ToString(),
            _ => null,
        };

        return string.IsNullOrWhiteSpace(text) ? null : text.Trim();
    }

    /// <summary>Reads a number whether the provider sent it as a JSON number or as a quoted string.</summary>
    private static double? Number(JsonElement element, string property)
    {
        if (!element.TryGetProperty(property, out var value))
        {
            return null;
        }

        return value.ValueKind switch
        {
            JsonValueKind.Number => value.TryGetDouble(out var number) ? number : null,
            JsonValueKind.String => double.TryParse(
                value.GetString(),
                NumberStyles.Float,
                CultureInfo.InvariantCulture,
                out var parsed) ? parsed : null,
            _ => null,
        };
    }

    /// <summary>
    /// ACLED publishes ISO 3166-1 alpha-3; the gazetteer and the rest of this system use alpha-2.
    /// Rather than carry a second country table, the three-letter code is dropped: it would not match
    /// anything downstream, and a wrong country code is worse than an absent one.
    /// </summary>
    private static string? NormaliseCountryCode(string? iso3) =>
        string.IsNullOrWhiteSpace(iso3) || iso3.Length != 2 ? null : iso3.ToUpperInvariant();

    private static DateTimeOffset? ParseDate(string? value) =>
        !string.IsNullOrWhiteSpace(value)
        && DateTimeOffset.TryParse(
            value,
            CultureInfo.InvariantCulture,
            DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal,
            out var parsed)
            ? parsed
            : null;
}
