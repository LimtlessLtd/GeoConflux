namespace Geopolitics.Domain;

/// <summary>
/// How much ground a resolved coordinate stands for.
/// <para>
/// This is domain information, not presentation detail, because correlation depends on it. Two
/// reports at the same city coordinate are evidence of the same place; two reports at the same
/// country centroid are evidence only that both named the same country, and the centroid they share
/// is an artefact of the lexicon rather than a measurement. A model that cannot tell those apart will
/// merge every report from a large country into one incident — which is exactly what happened before
/// this existed.
/// </para>
/// </summary>
public enum LocationPrecision
{
    /// <summary>A city or specific feature; the coordinate is close to what was reported.</summary>
    Settlement = 0,

    /// <summary>A named sea, strait, or region; the coordinate is representative rather than exact.</summary>
    Region,

    /// <summary>A whole country; the coordinate may be very far from the event.</summary>
    Country,

    /// <summary>Coordinates supplied by the source itself, describing the observation directly.</summary>
    Exact,
}
