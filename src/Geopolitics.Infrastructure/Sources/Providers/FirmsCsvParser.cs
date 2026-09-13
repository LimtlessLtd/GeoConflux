using System.Globalization;

namespace Geopolitics.Infrastructure.Sources.Providers;

/// <param name="Latitude">Detection centre latitude, as published by the provider.</param>
/// <param name="Longitude">Detection centre longitude, as published by the provider.</param>
/// <param name="Confidence">Detection confidence, normalised to 0-100.</param>
/// <param name="BrightnessKelvin">Channel brightness temperature, the headline intensity figure.</param>
/// <param name="RadiativePowerMegawatts">Fire radiative power, when the dataset reports it.</param>
/// <param name="AcquiredAt">Acquisition time in UTC.</param>
/// <param name="Satellite">Reporting platform, kept so one detection can be told from another.</param>
/// <param name="IsNight">
/// Whether the instrument recorded this on the night side. Carried because it is the cheapest
/// discriminator there is between agricultural burning, which is overwhelmingly a daytime activity,
/// and the things this system is actually looking for.
/// </param>
public sealed record FirmsHotspot(
    double Latitude,
    double Longitude,
    int Confidence,
    double BrightnessKelvin,
    double? RadiativePowerMegawatts,
    DateTimeOffset AcquiredAt,
    string Satellite,
    bool IsNight)
{
    /// <summary>
    /// Identity for a detection, which FIRMS does not assign one of. Position, time, and platform
    /// together identify a pixel observation, so the same row seen in two overlapping requests
    /// produces the same key and is recognised as the same detection rather than a second fire.
    /// </summary>
    public string Identifier => string.Create(
        CultureInfo.InvariantCulture,
        $"{Latitude:F5},{Longitude:F5},{AcquiredAt:yyyyMMddHHmm},{Satellite}");
}

/// <summary>
/// Reads the CSV published by the NASA FIRMS area API.
/// <para>
/// Column order differs between the MODIS and VIIRS datasets and NASA has added columns over time,
/// so the header row is read rather than assumed. Rows that do not parse are skipped instead of
/// failing the batch: one malformed line in a thousand-line response should cost one detection, not
/// the whole poll.
/// </para>
/// </summary>
public static class FirmsCsvParser
{
    /// <summary>
    /// Parses a FIRMS CSV response.
    /// </summary>
    /// <param name="document">Raw response body.</param>
    /// <returns>Every row that parsed into a usable detection.</returns>
    /// <exception cref="FormatException">
    /// The payload is not FIRMS CSV at all. FIRMS answers an invalid key or an exhausted quota with a
    /// plain-text message and HTTP 200, so a body without the expected columns is treated as a
    /// provider error rather than as zero detections, which would otherwise look like a quiet day.
    /// </exception>
    public static IReadOnlyList<FirmsHotspot> Parse(string document)
    {
        if (string.IsNullOrWhiteSpace(document))
        {
            throw new FormatException("The FIRMS response was empty.");
        }

        var lines = document.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        if (lines.Length == 0)
        {
            throw new FormatException("The FIRMS response contained no rows.");
        }

        var header = lines[0].Split(',').Select(column => column.Trim().ToLowerInvariant()).ToArray();
        var latitude = Array.IndexOf(header, "latitude");
        var longitude = Array.IndexOf(header, "longitude");
        var acquiredDate = Array.IndexOf(header, "acq_date");
        var acquiredTime = Array.IndexOf(header, "acq_time");

        if (latitude < 0 || longitude < 0 || acquiredDate < 0 || acquiredTime < 0)
        {
            throw new FormatException(
                $"The FIRMS response did not look like detection CSV. First line: '{Preview(lines[0])}'");
        }

        var confidence = Array.IndexOf(header, "confidence");
        var satellite = Array.IndexOf(header, "satellite");
        var power = Array.IndexOf(header, "frp");
        var dayNight = Array.IndexOf(header, "daynight");

        // VIIRS reports brightness as bright_ti4; MODIS reports it as brightness.
        var brightness = Array.IndexOf(header, "bright_ti4");

        if (brightness < 0)
        {
            brightness = Array.IndexOf(header, "brightness");
        }

        var hotspots = new List<FirmsHotspot>(lines.Length - 1);

        foreach (var line in lines.Skip(1))
        {
            var fields = line.Split(',');

            if (fields.Length < header.Length
                || !TryDouble(fields, latitude, out var parsedLatitude)
                || !TryDouble(fields, longitude, out var parsedLongitude)
                || !TryAcquisition(fields, acquiredDate, acquiredTime, out var acquiredAt))
            {
                continue;
            }

            if (parsedLatitude is < -90 or > 90 || parsedLongitude is < -180 or > 180)
            {
                continue;
            }

            hotspots.Add(new FirmsHotspot(
                parsedLatitude,
                parsedLongitude,
                ParseConfidence(Field(fields, confidence)),
                TryDouble(fields, brightness, out var parsedBrightness) ? parsedBrightness : 0,
                TryDouble(fields, power, out var parsedPower) ? parsedPower : null,
                acquiredAt,
                Field(fields, satellite) ?? "unknown",

                // "N" for night, "D" for day. An absent column reads as day, which is the
                // conservative answer: a night-only filter then rejects it rather than letting an
                // unknown detection through on a technicality.
                string.Equals(Field(fields, dayNight), "N", StringComparison.OrdinalIgnoreCase)));
        }

        return hotspots;
    }

    /// <summary>
    /// Normalises confidence to 0-100. VIIRS publishes the words low/nominal/high while MODIS
    /// publishes a percentage, and a single numeric scale is what lets one threshold configure both.
    /// The word mappings are the midpoints NASA documents for each band.
    /// </summary>
    private static int ParseConfidence(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return 0;
        }

        if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var numeric))
        {
            return Math.Clamp(numeric, 0, 100);
        }

        return value.Trim().ToLowerInvariant() switch
        {
            "h" or "high" => 90,
            "n" or "nominal" => 60,
            "l" or "low" => 20,
            _ => 0,
        };
    }

    /// <summary>
    /// Combines the separate date and time columns. FIRMS reports acquisition time as an integer
    /// HHMM in UTC, so 5 past midnight arrives as <c>5</c> and must be padded before it is read.
    /// </summary>
    private static bool TryAcquisition(string[] fields, int dateIndex, int timeIndex, out DateTimeOffset acquiredAt)
    {
        acquiredAt = default;
        var date = Field(fields, dateIndex);
        var time = Field(fields, timeIndex);

        if (string.IsNullOrWhiteSpace(date)
            || !DateOnly.TryParse(date, CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsedDate))
        {
            return false;
        }

        var minutesOfDay = 0;

        if (!string.IsNullOrWhiteSpace(time)
            && int.TryParse(time, NumberStyles.Integer, CultureInfo.InvariantCulture, out var packed)
            && packed is >= 0 and <= 2359)
        {
            minutesOfDay = ((packed / 100) * 60) + (packed % 100);
        }

        acquiredAt = new DateTimeOffset(parsedDate.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero)
            .AddMinutes(minutesOfDay);

        return true;
    }

    private static bool TryDouble(string[] fields, int index, out double value)
    {
        value = 0;
        var field = Field(fields, index);

        return !string.IsNullOrWhiteSpace(field)
            && double.TryParse(field, NumberStyles.Float, CultureInfo.InvariantCulture, out value);
    }

    private static string? Field(string[] fields, int index) =>
        index >= 0 && index < fields.Length ? fields[index].Trim() : null;

    private static string Preview(string line) => line.Length <= 120 ? line : line[..120];
}
