namespace Geopolitics.Application.Abstractions;

/// <summary>
/// What the place lexicon knows, for reporting rather than for resolving.
/// <para>
/// The resolver already answers "where is this name"; this answers "how much could you place at
/// all", which is a different and more uncomfortable question. It exists as an abstraction because
/// the lexicon lives in infrastructure and the coverage report is application logic, and the
/// dependency runs one way only.
/// </para>
/// </summary>
public interface IPlaceLexicon
{
    /// <summary>
    /// How many places are held per theatre, keyed by theatre name. This is the ceiling on what any
    /// text source can place there, which is why it is reported beside the observation counts rather
    /// than left for a reader to wonder about.
    /// </summary>
    IReadOnlyDictionary<string, int> PlacesByTheatre { get; }

    /// <summary>
    /// Names dropped because they denote more than one place. A countable, honest limit: every one
    /// of these is a name a report could use and this system would fail to place.
    /// </summary>
    int AmbiguousNameCount { get; }
}
