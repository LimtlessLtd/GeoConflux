namespace Geopolitics.Domain;

/// <summary>
/// Who an observation came from, in the only terms the pipeline is allowed to act on.
/// <para>
/// Three cases exist and they are not interchangeable, which is why they are reached through named
/// factories rather than by assembling a tier and two strings at each call site. A wire story has a
/// publisher and no channel. A Telegram post has a channel and no publisher. A submission through
/// the open write path has neither, and pretending otherwise would let an anonymous stranger corroborate
/// another anonymous stranger.
/// </para>
/// <para>
/// Deliberately carries no publisher name. The publisher is already the source name, and a second
/// copy of it here would be a field that can disagree with itself. What this type exists to answer
/// is the question the source name cannot: whether there is an identity behind this claim that
/// something else could be independent <em>of</em>.
/// </para>
/// </summary>
/// <param name="Tier">Published reporting, or a claim by an account.</param>
/// <param name="Platform">The open platform a post appeared on. Null for anything not a post.</param>
/// <param name="Channel">The account or channel that posted it. Null for anything not a post.</param>
public sealed record SourceAttribution(SourceTier Tier, string? Platform, string? Channel)
{
    /// <summary>A named organisation, a curated dataset, or an instrument. The ordinary case.</summary>
    public static SourceAttribution Published { get; } = new(SourceTier.Published, null, null);

    /// <summary>
    /// A claim with no identity behind it at all, which is what the open write path produces. It is
    /// user-generated because that is what it is, and it names no channel because there is none —
    /// the two together are what stop it counting as corroboration for anything.
    /// </summary>
    public static SourceAttribution Unattributed { get; } = new(SourceTier.UserGenerated, null, null);

    /// <summary>A post on an open platform by a named account.</summary>
    public static SourceAttribution Post(string platform, string channel)
    {
        if (string.IsNullOrWhiteSpace(platform))
        {
            throw new DomainException("A post must name the platform it appeared on.");
        }

        if (string.IsNullOrWhiteSpace(channel))
        {
            throw new DomainException("A post must name the channel that published it.");
        }

        return new SourceAttribution(SourceTier.UserGenerated, platform.Trim().ToLowerInvariant(), channel.Trim());
    }

    /// <summary>
    /// Whether this is a claim that may not form an incident on its own.
    /// </summary>
    public bool IsClaim => Tier == SourceTier.UserGenerated;

    /// <summary>
    /// The identity two claims must differ on before either corroborates the other, or
    /// <see langword="null"/> when there is no identity to compare.
    /// <para>
    /// Platform and channel together, because a handle is only unique within its platform and
    /// <c>telegram/reuters</c> is not <c>mastodon/reuters</c>. Null for an unattributed claim, and
    /// that null is load-bearing: it is what makes two anonymous submissions unable to corroborate
    /// each other however well their wording agrees.
    /// </para>
    /// </summary>
    public string? Identity => Platform is not null && Channel is not null
        ? $"{Platform}/{Channel}"
        : null;
}
