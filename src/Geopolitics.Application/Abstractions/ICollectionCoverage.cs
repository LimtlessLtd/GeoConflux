namespace Geopolitics.Application.Abstractions;

/// <param name="Channel">Platform and channel, as the attribution names it.</param>
/// <param name="Outcome">What came of asking: collected, nothing matched, no public posts, unreachable.</param>
/// <param name="Reason">Why, for the outcomes that have one.</param>
/// <param name="Read">Posts read before the brief's terms were applied.</param>
/// <param name="Matched">How many of those the brief's terms accepted.</param>
/// <param name="Collected">How many survived the diversity caps.</param>
public sealed record SourceOutcome(
    string Channel,
    string Outcome,
    string? Reason,
    int Read,
    int Matched,
    int Collected)
{
    /// <summary>
    /// Whether this source produced nothing, for any of the three reasons a source can produce
    /// nothing. Derived rather than stored, so a new outcome string from a newer tool is handled
    /// correctly by default instead of being silently counted as a success.
    /// </summary>
    public bool IsEmpty => Collected == 0;
}

/// <summary>
/// What the last collection runs asked, and what came back — including from the sources that gave
/// nothing.
/// <para>
/// This exists because the failure it detects is invisible otherwise. A dashboard that is eighty
/// percent one theatre looks exactly like a working global dashboard: the map has pins on it, the
/// pipeline is healthy, nothing errored. The only way to see the bias is to count, and the only
/// honest way to present breadth is to publish the count alongside the claim.
/// </para>
/// </summary>
public interface ICollectionCoverage
{
    /// <summary>
    /// Per-source outcomes from every readable bundle, newest run first. Empty when nothing has been
    /// collected, which is a different fact from every source having failed and is reported as one.
    /// </summary>
    IReadOnlyList<SourceOutcome> ReadOutcomes();
}
