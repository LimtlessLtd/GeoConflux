namespace Geopolitics.Application.Coverage;

/// <param name="Name">Short label used in the report and on the page.</param>
/// <param name="CountryCode">ISO 3166-1 alpha-2 code an observation must carry to count here.</param>
/// <param name="Bounds">
/// Latitude and longitude limits narrowing the theatre inside its country, or <see langword="null"/>
/// when the theatre is the whole country. Tigray needs this and the other two do not.
/// </param>
/// <param name="Caveat">
/// What a reader has to know before trusting a count here. Not a disclaimer in the legal sense: a
/// statement of what this theatre's data cannot tell them, which differs per theatre and is the
/// difference between a map that informs and one that misleads by omission.
/// </param>
public sealed record Theatre(
    string Name,
    string CountryCode,
    TheatreBounds? Bounds,
    string Caveat);

/// <param name="South">Southern limit in degrees.</param>
public sealed record TheatreBounds(double South, double North, double West, double East)
{
    public bool Contains(double latitude, double longitude) =>
        latitude >= South && latitude <= North && longitude >= West && longitude <= East;
}

/// <summary>
/// The three theatres this system is asked to cover, and what it cannot say about each.
/// <para>
/// These are here rather than in configuration because they are not a deployment choice. They are the
/// subject the current sprint is about, the gazetteer was extracted for exactly these three, and the
/// caveats below are findings that took research to establish rather than settings to be tuned.
/// </para>
/// </summary>
public static class Theatres
{
    /// <summary>
    /// Tigray's caveat is the one that matters most and it is stated at length on purpose.
    /// <para>
    /// The other two theatres are covered thinly in places. Tigray is covered thinly everywhere, and
    /// a reader looking at a nearly empty map of it would reasonably conclude that little is
    /// happening. The opposite is true, and the reasons are specific and checkable: dedicated ACLED
    /// coverage thinned roughly six months before the shooting restarted, and communications
    /// blackouts are a recurring feature of the conflict rather than an occasional accident.
    /// </para>
    /// </summary>
    private const string TigrayCaveat =
        "Coverage here is sparser than the conflict. The ACLED Ethiopia Peace Observatory ended its "
        + "fortnightly updates on 1 July 2025, folding Ethiopia into monthly regional overviews — "
        + "roughly six months before fighting resumed in January 2026. Communications blackouts are "
        + "a recurring feature of this conflict, and independent verification is thin. A quiet "
        + "district on this map means nobody reported, not that nothing happened.";

    private const string UkraineCaveat =
        "The best-covered of the three. Coded events carry their own coordinates and arrive with "
        + "several days of lag, so this shows what has been recorded rather than what is happening "
        + "now. Front-line settlements below the gazetteer's population floor go unplaced, and a few "
        + "towns — Kostiantynivka among them — share a name with other Ukrainian places and cannot "
        + "be resolved from the name alone.";

    private const string YemenCaveat =
        "Placed at district and governorate precision, not at settlement precision, because that is "
        + "what the reporting supports: the Yemen Data Project records the air war as governorate, "
        + "district and area, and states no coordinates at all. A marker here shows which district, "
        + "not where in it.";

    public static IReadOnlyList<Theatre> All { get; } =
    [
        new("Ukraine", "UA", null, UkraineCaveat),
        new("Yemen", "YE", null, YemenCaveat),

        // Tigray is a region rather than a country, so it is the one theatre that needs limits
        // inside its country. The box is the same one the gazetteer extract was validated against.
        new("Tigray", "ET", new TheatreBounds(12.0, 15.2, 36.2, 40.6), TigrayCaveat),
    ];
}
