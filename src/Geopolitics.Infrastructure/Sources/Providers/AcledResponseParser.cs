using System.Globalization;
using System.Text.Json;
using Geopolitics.Domain;

namespace Geopolitics.Infrastructure.Sources.Providers;

/// <param name="Identifier">ACLED's own event identifier, which is stable across their revisions.</param>
/// <param name="EventType">The category ACLED assigned, mapped onto this system's taxonomy.</param>
/// <param name="Latitude">Coordinates from ACLED's own coded record, not inferred from prose.</param>
/// <param name="CountryName">The country as ACLED names it. The schema carries no alpha-2 code to read.</param>
/// <param name="Precision">What ACLED says its own coordinates describe, from <c>geo_precision</c>.</param>
/// <param name="Fatalities">Reported fatalities, which drive the severity mapping.</param>
public sealed record AcledEvent(
    string Identifier,
    string Headline,
    string Notes,
    EventType EventType,
    Severity Severity,
    double? Latitude,
    double? Longitude,
    string? CountryName,
    string? LocationName,
    LocationPrecision Precision,
    DateTimeOffset? OccurredAt,
    int Fatalities);

/// <param name="Events">The rows this parser could make sense of.</param>
/// <param name="Truncated">
/// Whether ACLED returned as many rows as the request allowed, meaning there may be more it did not
/// send. See <see cref="AcledResponseParser.ParsePage"/> for why this is inferred rather than read.
/// </param>
public sealed record AcledPage(IReadOnlyList<AcledEvent> Events, bool Truncated);

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
    public static IReadOnlyList<AcledEvent> Parse(string document) => ParsePage(document, int.MaxValue).Events;

    /// <summary>
    /// Parses a response and says whether ACLED had more for the request than it sent.
    /// <para>
    /// ACLED publishes no "there is more" flag, so completeness is inferred the only way its API
    /// allows: a response holding as many rows as the request asked for is a response that may have
    /// been cut off at the limit. Judged on the rows ACLED <em>returned</em>, not the rows this
    /// parser could make sense of — a row skipped for having no identifier still consumed a slot in
    /// the limit, and counting only the usable ones would read a truncated page as a complete one.
    /// </para>
    /// </summary>
    /// <param name="requestedLimit">The <c>limit</c> sent with the request.</param>
    /// <exception cref="FormatException">The payload is not JSON, or is not an ACLED response.</exception>
    public static AcledPage ParsePage(string document, int requestedLimit)
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

            var returned = data.GetArrayLength();
            var events = new List<AcledEvent>(returned);

            foreach (var element in data.EnumerateArray())
            {
                if (element.ValueKind == JsonValueKind.Object && ReadEvent(element) is { } record)
                {
                    events.Add(record);
                }
            }

            return new AcledPage(events, returned >= requestedLimit);
        }
    }

    private static AcledEvent? ReadEvent(JsonElement element)
    {
        // event_id_cnty is the identifier the current schema publishes. The retired API also
        // returned a numeric data_id, and it is still accepted as a fallback so a recorded payload
        // from the old platform remains readable rather than silently producing nothing.
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
            country,
            string.IsNullOrWhiteSpace(location) ? country : location,
            MapPrecision(Number(element, "geo_precision")),
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
    /// Maps ACLED's <c>geo_precision</c> onto this system's precision scale.
    /// <para>
    /// Worth stating plainly, because the previous adapter declared every ACLED coordinate exact and
    /// that was never true. ACLED's own codebook describes its best case, code 1, as "the source
    /// reporting indicates a particular town, and coordinates are available for that town" — the
    /// town's coordinates, not the event's. That is a settlement fix, which is what this system calls
    /// <see cref="LocationPrecision.Settlement"/>. Code 2 covers activity "near a town or a city" or
    /// in "a small part of a region", and code 3 a larger region represented by a provincial capital
    /// or a natural feature; both are a representative point standing in for an area, which is
    /// <see cref="LocationPrecision.Region"/>.
    /// </para>
    /// <para>
    /// Codes 2 and 3 therefore collapse together even though 3 is considerably coarser. That loses a
    /// gradation, and the alternative — inventing a fourth level to keep it — would be adding a
    /// domain concept to carry one provider's scale. Understating precision is the safe direction for
    /// the error to run, so the gradation goes rather than the honesty.
    /// </para>
    /// </summary>
    private static LocationPrecision MapPrecision(double? geoPrecision) => geoPrecision switch
    {
        1 => LocationPrecision.Settlement,
        2 or 3 => LocationPrecision.Region,

        // ACLED codes this field on every record, so this is the defensive branch rather than a real
        // case. Settlement matches what the adapter gets when the field is present and best.
        _ => LocationPrecision.Settlement,
    };

    /// <summary>
    /// Derives severity from the fatality count ACLED coded, on the bands shared with UCDP so the two
    /// datasets mean the same thing on one map.
    /// <para>
    /// The one ACLED-specific departure is the zero-fatality case. A battle with no reported deaths is
    /// still a battle, and reading it as the quietest thing on the map would understate an armed
    /// engagement because nobody was confirmed killed in it.
    /// </para>
    /// </summary>
    private static Severity MapSeverity(int fatalities, string eventType) =>
        fatalities == 0 && eventType.Contains("Battle", StringComparison.OrdinalIgnoreCase)
            ? Severity.Medium
            : ConflictSeverity.FromDeaths(fatalities);

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
