namespace Geopolitics.Domain;

public sealed class GeoLocation
{
    private GeoLocation()
    {
        Name = string.Empty;
    }

    public GeoLocation(string name, string? countryCode, double latitude, double longitude)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new DomainException("A resolved location requires a name.");
        }

        if (latitude is < -90 or > 90)
        {
            throw new DomainException("Latitude must be between -90 and 90 degrees.");
        }

        if (longitude is < -180 or > 180)
        {
            throw new DomainException("Longitude must be between -180 and 180 degrees.");
        }

        Name = name.Trim();
        CountryCode = string.IsNullOrWhiteSpace(countryCode) ? null : countryCode.Trim().ToUpperInvariant();
        Latitude = latitude;
        Longitude = longitude;
    }

    public string Name { get; private set; }

    public string? CountryCode { get; private set; }

    public double Latitude { get; private set; }

    public double Longitude { get; private set; }

    /// <summary>
    /// Returns an independent copy. <see cref="GeoLocation"/> is a value object, so two entities that
    /// happen to be at the same place should hold equal values rather than share one instance.
    /// </summary>
    public GeoLocation Copy() => new(Name, CountryCode, Latitude, Longitude);

    /// <summary>
    /// Great-circle distance in kilometres. Correlation needs to know whether two reports
    /// describe the same place, and a spherical approximation is accurate enough for the
    /// tens-of-kilometres thresholds the correlator uses.
    /// </summary>
    public double DistanceInKilometresTo(GeoLocation other)
    {
        ArgumentNullException.ThrowIfNull(other);

        const double earthRadiusKm = 6371.0088;
        var latitudeDelta = ToRadians(other.Latitude - Latitude);
        var longitudeDelta = ToRadians(other.Longitude - Longitude);
        var startLatitude = ToRadians(Latitude);
        var endLatitude = ToRadians(other.Latitude);

        var haversine = (Math.Sin(latitudeDelta / 2) * Math.Sin(latitudeDelta / 2))
            + (Math.Cos(startLatitude) * Math.Cos(endLatitude) * Math.Sin(longitudeDelta / 2) * Math.Sin(longitudeDelta / 2));

        return 2 * earthRadiusKm * Math.Asin(Math.Min(1, Math.Sqrt(haversine)));
    }

    private static double ToRadians(double degrees) => degrees * Math.PI / 180;
}
