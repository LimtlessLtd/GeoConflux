using Geopolitics.Application.Abstractions;

namespace Geopolitics.Infrastructure.Location;

/// <summary>
/// The maritime passages this system watches.
/// <para>
/// A short, curated list rather than a comprehensive one. These are the passages where a local
/// disruption has a disproportionate effect because there is no practical alternative route, which is
/// what makes proximity to them worth reporting at all. Adding every strait on the planet would turn
/// a watch list into a lookup table and make the dashboard's chokepoint panel meaningless.
/// </para>
/// <para>
/// Positions are representative points on each passage, consistent with the gazetteer entries of the
/// same name. They are adequate for "is this incident at this chokepoint" at the radii below, and
/// they are not survey data.
/// </para>
/// </summary>
public sealed class MaritimeChokepointCatalogue : IChokepointCatalogue
{
    /// <summary>
    /// Radii differ because the features do. A canal is a few kilometres wide, so a tight radius
    /// keeps unrelated coastal activity out; a strait and its approaches span far more water, and too
    /// tight a radius there would miss the reports that matter most.
    /// </summary>
    private static readonly MaritimeChokepoint[] Entries =
    [
        new(
            "Bab-el-Mandeb",
            "Southern gate of the Red Sea, carrying traffic between the Suez route and the Indian Ocean.",
            12.585,
            43.334,
            120),
        new(
            "Strait of Hormuz",
            "The only sea route out of the Persian Gulf.",
            26.567,
            56.250,
            120),
        new(
            "Suez Canal",
            "Sea-level canal linking the Mediterranean to the Red Sea, with no alternative short of rounding Africa.",
            30.500,
            32.350,
            60),
        new(
            "Strait of Malacca",
            "Principal passage between the Indian Ocean and the South China Sea.",
            3.000,
            100.500,
            150),
        new(
            "Taiwan Strait",
            "Passage between Taiwan and the mainland, carrying a large share of northeast Asian traffic.",
            24.500,
            119.500,
            150),
        new(
            "Panama Canal",
            "Lock canal linking the Atlantic and Pacific.",
            9.080,
            -79.680,
            60),
        new(
            "Kerch Strait",
            "Sole connection between the Black Sea and the Sea of Azov.",
            45.300,
            36.500,
            60),
        new(
            "Gulf of Aden",
            "Approach to Bab-el-Mandeb and a long-standing focus of counter-piracy escort activity.",
            12.500,
            47.500,
            200),
    ];

    public IReadOnlyList<MaritimeChokepoint> Chokepoints => Entries;
}
