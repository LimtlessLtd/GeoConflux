using System.Globalization;
using System.Text.Json;
using Geopolitics.Domain;

namespace Geopolitics.Infrastructure.Sources.Providers;

/// <param name="Identifier">UCDP's own event identifier, stable across revisions of the dataset.</param>
/// <param name="Latitude">Coordinates from UCDP's own coded record, not inferred from prose.</param>
/// <param name="Precision">What UCDP says its coordinates describe, from <c>where_prec</c>.</param>
/// <param name="CountryName">The country as UCDP names it. The schema carries no alpha-2 code.</param>
/// <param name="Deaths">The <c>best</c> estimate of deaths, which drives the severity mapping.</param>
public sealed record UcdpEvent(
    string Identifier,
    string Headline,
    string Notes,
    EventType EventType,
    Severity Severity,
    double? Latitude,
    double? Longitude,
    LocationPrecision Precision,
    string? CountryName,
    string? LocationName,
    DateTimeOffset? OccurredAt,
    int Deaths);

/// <param name="Events">The rows this parser could make sense of.</param>
/// <param name="Truncated">Whether UCDP's own envelope says a further page exists.</param>
public sealed record UcdpPage(IReadOnlyList<UcdpEvent> Events, bool Truncated);

/// <summary>
/// Reads the JSON returned by the UCDP API.
/// <para>
/// Tolerant in the same way and for the same reason as the ACLED parser: this is a third-party
/// contract this repository cannot pin, numbers arrive sometimes as JSON numbers and sometimes as
/// quoted strings, and a row that cannot be made sense of is skipped rather than guessed at. A
/// response that is not a UCDP response at all is an error.
/// </para>
/// </summary>
public static class UcdpResponseParser
{
    /// <summary>
    /// Parses a UCDP resource response.
    /// </summary>
    /// <exception cref="FormatException">The payload is not JSON, or is not a UCDP response.</exception>
    public static IReadOnlyList<UcdpEvent> Parse(string document) => ParsePage(document).Events;

    /// <summary>
    /// Parses a response and says whether UCDP had more for the request than it sent.
    /// <para>
    /// Unlike ACLED this needs no inference. UCDP's envelope states the total number of pages and
    /// carries a link to the next one, so a request that asked for the first page and got back a
    /// non-empty <c>NextPageUrl</c> is definitively incomplete. The link is read as a flag and not
    /// followed: a URL supplied by a response is not a URL this process should dial, which is the
    /// same reason redirects are judged at connection time rather than trusted from the payload.
    /// </para>
    /// </summary>
    /// <exception cref="FormatException">The payload is not JSON, or is not a UCDP response.</exception>
    public static UcdpPage ParsePage(string document)
    {
        if (string.IsNullOrWhiteSpace(document))
        {
            throw new FormatException("The UCDP response was empty.");
        }

        JsonDocument parsed;

        try
        {
            parsed = JsonDocument.Parse(document);
        }
        catch (JsonException exception)
        {
            throw new FormatException($"The UCDP response was not valid JSON: {exception.Message}", exception);
        }

        using (parsed)
        {
            var root = parsed.RootElement;

            if (root.ValueKind != JsonValueKind.Object)
            {
                throw new FormatException("The UCDP response was not a JSON object.");
            }

            if (!root.TryGetProperty("Result", out var result) || result.ValueKind != JsonValueKind.Array)
            {
                throw new FormatException("The UCDP response contained no Result array.");
            }

            var events = new List<UcdpEvent>(result.GetArrayLength());

            foreach (var element in result.EnumerateArray())
            {
                if (element.ValueKind == JsonValueKind.Object && ReadEvent(element) is { } record)
                {
                    events.Add(record);
                }
            }

            return new UcdpPage(events, HasFurtherPages(root));
        }
    }

    /// <summary>
    /// Whether the envelope says another page exists. Either signal is enough, and both are checked
    /// because a field that is absent from a future revision should degrade to the other rather than
    /// silently start reporting every response as complete.
    /// </summary>
    private static bool HasFurtherPages(JsonElement root)
    {
        if (root.TryGetProperty("NextPageUrl", out var next)
            && next.ValueKind == JsonValueKind.String
            && !string.IsNullOrWhiteSpace(next.GetString()))
        {
            return true;
        }

        return root.TryGetProperty("TotalPages", out var totalPages)
            && Number(totalPages) is > 1;
    }

    private static double? Number(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.Number when element.TryGetDouble(out var value) => value,
        JsonValueKind.String when double.TryParse(
            element.GetString(),
            NumberStyles.Float,
            CultureInfo.InvariantCulture,
            out var parsed) => parsed,
        _ => null,
    };

    private static UcdpEvent? ReadEvent(JsonElement element)
    {
        var identifier = Text(element, "id") ?? Text(element, "relid");

        if (string.IsNullOrWhiteSpace(identifier))
        {
            return null;
        }

        var country = Text(element, "country");
        var deaths = (int)Math.Clamp(Number(element, "best") ?? 0, 0, int.MaxValue);
        var violence = Number(element, "type_of_violence");

        // where_coordinates is UCDP's standardised name for the place the event is assigned to, which
        // is the most specific name in the record. The administrative divisions are the fallbacks, in
        // order of how much they narrow things down.
        var place = Text(element, "where_coordinates")
            ?? Text(element, "adm_2")
            ?? Text(element, "adm_1")
            ?? country;

        var conflict = Text(element, "conflict_name");
        var headline = Text(element, "source_headline")
            ?? (string.IsNullOrWhiteSpace(place) ? conflict : $"{Describe(violence)} in {place}");

        var latitude = Number(element, "latitude");
        var longitude = Number(element, "longitude");

        // A coordinate pair is only usable whole. Half a pair from a provider is a data fault, and
        // passing it on would place the event on the equator or the prime meridian.
        if (latitude is null || longitude is null || latitude is < -90 or > 90 || longitude is < -180 or > 180)
        {
            latitude = null;
            longitude = null;
        }

        if (string.IsNullOrWhiteSpace(headline))
        {
            return null;
        }

        return new UcdpEvent(
            identifier.Trim(),
            headline,
            Notes(element, conflict, deaths),
            MapEventType(violence),
            ConflictSeverity.FromDeaths(deaths),
            latitude,
            longitude,
            MapPrecision(Number(element, "where_prec")),
            country,
            place,
            ParseDate(Text(element, "date_start") ?? Text(element, "date_end")),
            deaths);
    }

    /// <summary>
    /// Builds the text the pipeline reads. UCDP records are coded summaries rather than articles, so
    /// what is available is the conflict, the parties, and a death count — stated plainly, because
    /// this text is what the classifier and the deduplicator work on.
    /// </summary>
    private static string Notes(JsonElement element, string? conflict, int deaths)
    {
        var parts = new List<string>(4);

        if (Text(element, "source_article") is { } article)
        {
            parts.Add(article);
        }

        var sideA = Text(element, "side_a");
        var sideB = Text(element, "side_b");

        if (sideA is not null && sideB is not null)
        {
            parts.Add($"Recorded between {sideA} and {sideB}.");
        }

        if (conflict is not null)
        {
            parts.Add($"Part of the {conflict} conflict as coded by UCDP.");
        }

        if (deaths > 0)
        {
            parts.Add(string.Create(CultureInfo.InvariantCulture, $"Best estimate of deaths: {deaths}."));
        }

        return parts.Count == 0 ? "A conflict event coded by UCDP." : string.Join(' ', parts);
    }

    /// <summary>
    /// Maps UCDP's <c>where_prec</c> onto this system's precision scale, from the GED codebook.
    /// <para>
    /// This is the property that makes UCDP worth having alongside ACLED rather than instead of it. A
    /// borrowed coordinate whose precision travels with it can be drawn honestly; one without has to
    /// be either trusted too much or discarded.
    /// </para>
    /// <para>
    /// The codebook's own definitions: 1 is "exact location of the event known and coded", which is
    /// the one case in this system that genuinely earns <see cref="LocationPrecision.Exact"/>. 2 is
    /// within "a ca. 25 km radius around a known point", with the known point coded — a settlement
    /// fix. 3 and 4 are the second- and first-order administrative divisions represented by a point,
    /// "typically the centroid"; 5 is a linear feature or fuzzy polygon given a representation point;
    /// and 7 is "event in international waters or airspace", which is what this lexicon already means
    /// by a named sea. All four are a representative point for an area rather than a position, which
    /// is <see cref="LocationPrecision.Region"/>. 6 is the country alone.
    /// </para>
    /// <para>
    /// Codes 3, 4, 5 and 7 therefore collapse together, which loses the gradation between a district
    /// centroid and a whole river. Carrying it would mean adding domain concepts to express one
    /// provider's scale, and understating precision is the safe direction for the error to run.
    /// </para>
    /// </summary>
    private static LocationPrecision MapPrecision(double? wherePrecision) => wherePrecision switch
    {
        1 => LocationPrecision.Exact,
        2 => LocationPrecision.Settlement,
        3 or 4 or 5 or 7 => LocationPrecision.Region,
        6 => LocationPrecision.Country,

        // The codebook notes where_prec is only populated for data collected since 2013. An absent
        // value is therefore a real case rather than a defensive branch, and the honest reading of
        // "the provider did not say" is not "exact".
        _ => LocationPrecision.Region,
    };

    /// <summary>
    /// Maps UCDP's <c>type_of_violence</c> onto this system's taxonomy. One-sided violence is
    /// violence against civilians by an organised actor, which is the same category ACLED's
    /// "violence against civilians" maps to, so the two sources agree on the map.
    /// </summary>
    private static EventType MapEventType(double? typeOfViolence) => typeOfViolence switch
    {
        1 or 2 => EventType.Conflict,
        3 => EventType.Terrorism,
        _ => EventType.Other,
    };

    private static string Describe(double? typeOfViolence) => typeOfViolence switch
    {
        1 => "State-based conflict event",
        2 => "Non-state conflict event",
        3 => "One-sided violence against civilians",
        _ => "Recorded conflict event",
    };

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
