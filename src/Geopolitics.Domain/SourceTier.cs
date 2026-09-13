namespace Geopolitics.Domain;

/// <summary>
/// What kind of thing said this: an organisation that can be held to it, or an account.
/// <para>
/// A separate axis from <see cref="ObservationKind"/>, which records the shape of the intake — a
/// feed, an instrument, a dataset, a submission. That answers "how did this arrive". This answers
/// "who is standing behind it", and the two do not track each other: a Telegram post and a wire
/// story both arrive as text through the same door, and nothing about that door distinguishes them.
/// </para>
/// <para>
/// The tiers are the plan's. Tier A is open documents, Tier B open social, and Tier C the platforms
/// that gate access. Tier C has no value here on purpose: a source this project cannot legitimately
/// read produces no observations, so a tier for it would be a value nothing can ever hold. It is
/// recorded as a coverage gap instead, which is the only honest place for it.
/// </para>
/// </summary>
public enum SourceTier
{
    /// <summary>
    /// Tier A. A named organisation with an editorial process behind it, a curated dataset, or an
    /// instrument. Zero so that a record written before this existed, and any adapter that forgets
    /// to say, reads as the ordinary case rather than as a claim needing corroboration — the
    /// alternative would quietly hold back wire reporting nobody meant to gate.
    /// </summary>
    Published = 0,

    /// <summary>
    /// Tier B. An account posting on an open platform, or a stranger submitting through the open
    /// write path. It may be the fastest and best account of an event, or an anonymous claim, or
    /// footage recycled from a different war, and nothing about the item alone distinguishes those.
    /// A single one of these may not form an incident.
    /// </summary>
    UserGenerated,
}
