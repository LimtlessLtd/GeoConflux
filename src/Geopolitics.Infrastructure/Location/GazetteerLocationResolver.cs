using System.Collections.Frozen;
using Geopolitics.Application.Abstractions;
using Geopolitics.Domain;
using Microsoft.Extensions.Logging;

namespace Geopolitics.Infrastructure.Location;

/// <summary>
/// Supplies coordinates from provider-declared values or a small built-in gazetteer of places that
/// recur in geopolitical reporting.
/// <para>
/// Per ADR 005 this is the only component permitted to produce latitude and longitude. A language
/// model may propose a place <em>name</em>; if that name is not present here, the observation is
/// reported as unresolved rather than given plausible-looking coordinates. Keeping the gazetteer
/// local also means the pipeline needs no geocoding credentials to run.
/// </para>
/// </summary>
public sealed partial class GazetteerLocationResolver(ILogger<GazetteerLocationResolver> logger) : ILocationResolver
{
    /// <param name="Latitude">Representative latitude of the place.</param>
    /// <param name="Longitude">Representative longitude of the place.</param>
    /// <param name="CountryCode">ISO 3166-1 alpha-2 code, where the place sits in one country.</param>
    private sealed record GazetteerEntry(string CanonicalName, double Latitude, double Longitude, string? CountryCode);

    /// <summary>
    /// Chokepoints, seas, and cities that appear repeatedly in maritime and conflict reporting.
    /// Coordinates are representative centroids, adequate for a globe view at these zoom levels.
    /// </summary>
    private static readonly FrozenDictionary<string, GazetteerEntry> Gazetteer = BuildGazetteer();

    public Task<LocationResolution> ResolveAsync(LocationResolutionRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();

        // A structured provider's own coordinates outrank any name lookup: they describe the exact
        // observation, whereas a gazetteer entry is only the centroid of a named area.
        if (request.DeclaredLatitude is { } latitude && request.DeclaredLongitude is { } longitude)
        {
            var name = string.IsNullOrWhiteSpace(request.LocationName)
                ? FormatCoordinates(latitude, longitude)
                : request.LocationName.Trim();

            return Task.FromResult(new LocationResolution(
                new GeoLocation(name, request.DeclaredCountryCode, latitude, longitude),
                LocationResolutionMethod.SourceProvided,
                0.95,
                null));
        }

        if (string.IsNullOrWhiteSpace(request.LocationName))
        {
            return Task.FromResult(LocationResolution.Failed("The observation named no location."));
        }

        var key = NormaliseKey(request.LocationName);

        if (Gazetteer.TryGetValue(key, out var entry))
        {
            return Task.FromResult(new LocationResolution(
                new GeoLocation(entry.CanonicalName, entry.CountryCode ?? request.DeclaredCountryCode, entry.Latitude, entry.Longitude),
                LocationResolutionMethod.Gazetteer,

                // A centroid for a named region is genuinely less precise than a provider fix,
                // and the confidence reported to the UI says so.
                0.7,
                null));
        }

        LogUnknownPlace(logger, request.LocationName);
        return Task.FromResult(LocationResolution.Failed($"'{request.LocationName.Trim()}' is not in the local gazetteer."));
    }

    private static string FormatCoordinates(double latitude, double longitude) =>
        $"{latitude:F3}, {longitude:F3}";

    /// <summary>Case- and punctuation-insensitive lookup key, so "Bab el Mandeb" matches "Bab-el-Mandeb".</summary>
    private static string NormaliseKey(string value)
    {
        Span<char> buffer = value.Length <= 128 ? stackalloc char[value.Length] : new char[value.Length];
        var length = 0;

        foreach (var character in value)
        {
            if (char.IsLetterOrDigit(character))
            {
                buffer[length++] = char.ToLowerInvariant(character);
            }
        }

        return new string(buffer[..length]);
    }

    private static FrozenDictionary<string, GazetteerEntry> BuildGazetteer()
    {
        GazetteerEntry[] entries =
        [
            new("Bab-el-Mandeb", 12.585, 43.334, "DJ"),
            new("Strait of Hormuz", 26.567, 56.250, "OM"),
            new("Suez Canal", 30.500, 32.350, "EG"),
            new("Red Sea", 20.000, 38.000, null),
            new("Gulf of Aden", 12.500, 47.500, null),
            new("Black Sea", 43.000, 34.000, null),
            new("Eastern Mediterranean", 34.700, 33.900, null),
            new("South China Sea", 13.000, 114.000, null),
            new("Taiwan Strait", 24.500, 119.500, null),
            new("Strait of Malacca", 3.000, 100.500, "MY"),
            new("Gulf of Guinea", 3.000, 3.000, null),
            new("Persian Gulf", 26.500, 51.500, null),
            new("Baltic Sea", 57.500, 19.500, null),
            new("Kerch Strait", 45.300, 36.500, null),
            new("Panama Canal", 9.080, -79.680, "PA"),
            new("Sea of Japan", 40.000, 135.000, null),
            new("Kyiv", 50.450, 30.523, "UA"),
            new("Odesa", 46.482, 30.723, "UA"),
            new("Moscow", 55.756, 37.617, "RU"),
            new("Beijing", 39.904, 116.407, "CN"),
            new("Taipei", 25.033, 121.565, "TW"),
            new("Tehran", 35.689, 51.389, "IR"),
            new("Sanaa", 15.369, 44.191, "YE"),
            new("Aden", 12.785, 45.019, "YE"),
            new("Djibouti", 11.572, 43.145, "DJ"),
            new("Cairo", 30.044, 31.236, "EG"),
            new("Beirut", 33.888, 35.495, "LB"),
            new("Damascus", 33.513, 36.292, "SY"),
            new("Jerusalem", 31.769, 35.217, "IL"),
            new("Gaza", 31.504, 34.466, "PS"),
            new("Baghdad", 33.315, 44.366, "IQ"),
            new("Riyadh", 24.713, 46.675, "SA"),
            new("Ankara", 39.933, 32.859, "TR"),
            new("Istanbul", 41.008, 28.978, "TR"),
            new("Khartoum", 15.501, 32.559, "SD"),
            new("Mogadishu", 2.047, 45.318, "SO"),
            new("Bamako", 12.639, -8.003, "ML"),
            new("Lagos", 6.524, 3.379, "NG"),
            new("Manila", 14.600, 120.984, "PH"),
            new("Seoul", 37.567, 126.978, "KR"),
            new("Pyongyang", 39.039, 125.762, "KP"),
            new("Tokyo", 35.690, 139.692, "JP"),
            new("New Delhi", 28.614, 77.209, "IN"),
            new("Islamabad", 33.684, 73.048, "PK"),
            new("Kabul", 34.556, 69.208, "AF"),
            new("Caracas", 10.481, -66.904, "VE"),
            new("Port-au-Prince", 18.594, -72.307, "HT"),
            new("Brussels", 50.851, 4.352, "BE"),
            new("Warsaw", 52.230, 21.011, "PL"),
            new("Vilnius", 54.687, 25.280, "LT"),
            new("Helsinki", 60.170, 24.938, "FI"),
            new("Washington", 38.895, -77.037, "US"),
            new("London", 51.507, -0.128, "GB"),
            new("Paris", 48.857, 2.352, "FR"),
            new("Berlin", 52.520, 13.405, "DE"),
        ];

        var map = new Dictionary<string, GazetteerEntry>(StringComparer.Ordinal);

        foreach (var entry in entries)
        {
            map[NormaliseKey(entry.CanonicalName)] = entry;
        }

        // Common alternates so ordinary reporting language resolves without an exact canonical match.
        (string Alias, string Canonical)[] aliases =
        [
            ("Bab al-Mandab", "Bab-el-Mandeb"),
            ("Bab el Mandeb", "Bab-el-Mandeb"),
            ("Hormuz", "Strait of Hormuz"),
            ("Malacca Strait", "Strait of Malacca"),
            ("Kiev", "Kyiv"),
            ("Odessa", "Odesa"),
            ("Sana'a", "Sanaa"),
            ("Gaza Strip", "Gaza"),
            ("Washington DC", "Washington"),
            ("Mediterranean", "Eastern Mediterranean"),
            ("Levant", "Eastern Mediterranean"),
        ];

        foreach (var (alias, canonical) in aliases)
        {
            map[NormaliseKey(alias)] = map[NormaliseKey(canonical)];
        }

        return map.ToFrozenDictionary(StringComparer.Ordinal);
    }

    [LoggerMessage(Level = LogLevel.Debug, Message = "No gazetteer entry for '{LocationName}'; the observation will be stored without coordinates.")]
    private static partial void LogUnknownPlace(ILogger logger, string locationName);
}
