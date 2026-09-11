namespace Geopolitics.Domain;

/// <summary>
/// A latitude/longitude rectangle used to narrow a spatial search before exact distances are
/// computed.
/// <para>
/// This exists because a database cannot index a great-circle distance. It can index latitude and
/// longitude, so the cheap, indexable rectangle around a search circle is pushed to SQL and only the
/// rows inside it are measured properly. The rectangle always contains the circle, never the reverse,
/// so the pre-filter can return rows that turn out to be too far away but can never hide one that
/// was close enough.
/// </para>
/// </summary>
public sealed record GeoBoundingBox
{
    private const double EarthRadiusKm = 6371.0088;

    private GeoBoundingBox(double south, double north, double west, double east, bool crossesAntimeridian)
    {
        South = south;
        North = north;
        West = west;
        East = east;
        CrossesAntimeridian = crossesAntimeridian;
    }

    public double South { get; }

    public double North { get; }

    /// <summary>Western edge. Greater than <see cref="East"/> when the box wraps the antimeridian.</summary>
    public double West { get; }

    public double East { get; }

    /// <summary>
    /// True when the box spans the 180th meridian, so its longitude range is two intervals rather
    /// than one. A caller that ignores this silently searches the long way round the planet.
    /// </summary>
    public bool CrossesAntimeridian { get; }

    /// <summary>
    /// The smallest rectangle guaranteed to contain every point within <paramref name="radiusKm"/>
    /// of the centre.
    /// </summary>
    /// <remarks>
    /// Latitude is simple: a degree of latitude is the same length everywhere. Longitude is not — the
    /// meridians converge, so a fixed distance spans more degrees the further from the equator you
    /// are, which is why the longitude delta divides by the cosine of the latitude. Near a pole that
    /// cosine approaches zero and the span covers the whole planet, which is correct rather than a
    /// degenerate case: a circle that reaches the pole really does contain every longitude.
    /// </remarks>
    public static GeoBoundingBox FromRadius(double latitude, double longitude, double radiusKm)
    {
        if (latitude is < -90 or > 90)
        {
            throw new DomainException("Latitude must be between -90 and 90 degrees.");
        }

        if (longitude is < -180 or > 180)
        {
            throw new DomainException("Longitude must be between -180 and 180 degrees.");
        }

        if (radiusKm <= 0)
        {
            throw new DomainException("A search radius must be greater than zero.");
        }

        var latitudeDelta = radiusKm / EarthRadiusKm * (180 / Math.PI);
        var south = latitude - latitudeDelta;
        var north = latitude + latitudeDelta;

        // Once the circle reaches a pole there is no meaningful longitude range left to filter on.
        if (south <= -90 || north >= 90)
        {
            return new GeoBoundingBox(Math.Max(-90, south), Math.Min(90, north), -180, 180, false);
        }

        // Widest point of the circle, which is the edge nearest the equator.
        var widestLatitude = Math.Min(Math.Abs(south), Math.Abs(north));
        var longitudeDelta = latitudeDelta / Math.Cos(widestLatitude * Math.PI / 180);

        if (longitudeDelta >= 180)
        {
            return new GeoBoundingBox(south, north, -180, 180, false);
        }

        var west = longitude - longitudeDelta;
        var east = longitude + longitudeDelta;
        var wraps = west < -180 || east > 180;

        return new GeoBoundingBox(south, north, Normalise(west), Normalise(east), wraps);
    }

    /// <summary>
    /// Whether a point falls inside this box. Used by in-memory callers; the database applies the
    /// same rule as a SQL predicate.
    /// </summary>
    public bool Contains(double latitude, double longitude)
    {
        if (latitude < South || latitude > North)
        {
            return false;
        }

        // A wrapped box is two intervals, [West, 180] and [-180, East], so the test is an "or"
        // rather than the usual "and". Getting this backwards excludes everything.
        return CrossesAntimeridian
            ? longitude >= West || longitude <= East
            : longitude >= West && longitude <= East;
    }

    /// <summary>Folds a longitude back into [-180, 180].</summary>
    private static double Normalise(double longitude)
    {
        var wrapped = (longitude + 180) % 360;
        return (wrapped < 0 ? wrapped + 360 : wrapped) - 180;
    }
}
