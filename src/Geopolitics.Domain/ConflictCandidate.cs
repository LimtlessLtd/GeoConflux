namespace Geopolitics.Domain;

/// <summary>
/// What is known about one report when asking which conflicts it belongs to.
/// <para>
/// A projection rather than the observation itself, so the predicate can be asked about a
/// hypothetical as easily as about a stored row, and so it is obvious at a glance exactly which four
/// facts membership rests on.
/// </para>
/// </summary>
/// <param name="CodedConflictKey">
/// The conflict the source's own coding named, where it named one. Present for UCDP records and
/// absent for everything else, which is most of the volume.
/// </param>
/// <param name="CountryCode">ISO 3166-1 alpha-2 of where this was placed, when it was placed.</param>
/// <param name="PlaceName">
/// The place it was placed at, as the gazetteer names it. Compared against the places a conflict has
/// had coded events at, so it is normalised the same way both sides are.
/// </param>
/// <param name="ActorNames">
/// Actors the report names. These come from extraction over untrusted text and are treated as claims
/// about the text, which is all a membership test needs them to be.
/// </param>
/// <param name="EventType">
/// What kind of event this was classified as. Used only to exclude, never to include.
/// </param>
public sealed record ConflictCandidate(
    string? CodedConflictKey = null,
    string? CountryCode = null,
    string? PlaceName = null,
    IReadOnlyList<string>? ActorNames = null,
    EventType EventType = EventType.Other);
