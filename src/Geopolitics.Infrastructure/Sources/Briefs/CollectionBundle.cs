namespace Geopolitics.Infrastructure.Sources.Briefs;

/// <summary>
/// What kind of thing a collected item is. This is a statement about the source, not a judgement
/// about the claim, which is why a collector is permitted to set it.
/// </summary>
public enum CollectedItemKind
{
    /// <summary>Published by a named organisation with an editorial process behind it.</summary>
    Document = 0,

    /// <summary>
    /// Posted by an account on a platform. It may be the fastest and best account of an event, or an
    /// anonymous claim, or footage recycled from a different war. The pipeline does not decide which
    /// from the item alone, so a single uncorroborated one may not form an incident.
    /// </summary>
    UserGenerated,
}

/// <summary>
/// One recorded collection run: what was looked for, when, and what was found.
/// </summary>
/// <param name="BundleId">Stable identifier for this run.</param>
/// <param name="CollectedAt">When the run happened. Displayed, because a bundle is a point-in-time capture.</param>
/// <param name="BriefId">The standing brief this run was tasked against.</param>
/// <param name="BriefRevision">Which revision of that brief, so two runs are comparable.</param>
/// <param name="Items">Items that survived validation.</param>
public sealed record CollectionBundle(
    string BundleId,
    DateTimeOffset CollectedAt,
    string BriefId,
    int BriefRevision,
    IReadOnlyList<CollectedItem> Items);

/// <summary>
/// One cited item. Everything here is either a fact about retrieval or a verbatim quotation; nothing
/// is an interpretation, because a collector does not interpret.
/// </summary>
/// <param name="Kind">Document or user-generated. Selects which attribution fields are present.</param>
/// <param name="Url">Where the item was read. Also its identity for deduplication.</param>
/// <param name="Publisher">Organisation that published it. Present for a document.</param>
/// <param name="Platform">Platform it was posted on. Present for user-generated material.</param>
/// <param name="Channel">Account or channel that posted it. Present for user-generated material.</param>
/// <param name="Title">Headline, where the source has one.</param>
/// <param name="PublishedAt">When the source says it was published or posted.</param>
/// <param name="RetrievedAt">When the collector actually fetched it.</param>
/// <param name="ContentHash">sha256 of the excerpt as recorded, so an edited quotation is detectable offline.</param>
/// <param name="Language">BCP-47 tag for the original text, where the collector could determine it.</param>
/// <param name="Excerpt">Contiguous verbatim quotation, in the original script.</param>
/// <param name="PlaceNames">Place names appearing verbatim in the text. Candidates for the gazetteer, never coordinates.</param>
/// <param name="RelatedTo">URLs the collector believes describe the same event. Advisory; the correlator decides.</param>
public sealed record CollectedItem(
    CollectedItemKind Kind,
    Uri Url,
    string? Publisher,
    string? Platform,
    string? Channel,
    string? Title,
    DateTimeOffset PublishedAt,
    DateTimeOffset RetrievedAt,
    string ContentHash,
    string? Language,
    string Excerpt,
    IReadOnlyList<string> PlaceNames,
    IReadOnlyList<string> RelatedTo)
{
    /// <summary>
    /// Who this came from, in the form the source name and the dashboard both use. A publisher and a
    /// channel are deliberately not interchangeable strings: a reader must never have to guess which
    /// they are looking at.
    /// </summary>
    public string Attribution => Kind == CollectedItemKind.Document
        ? Publisher ?? "unattributed"
        : $"{Platform}/{Channel}";
}

/// <summary>
/// Outcome of reading one bundle file: what was accepted, and why anything else was not.
/// </summary>
/// <param name="Bundle">The bundle, holding only items that passed validation.</param>
/// <param name="RejectedItems">One stated reason per skipped item, for logging rather than for display.</param>
public sealed record CollectionBundleReadResult(
    CollectionBundle Bundle,
    IReadOnlyList<string> RejectedItems);

/// <summary>
/// Caps applied while parsing. Passed in rather than read from configuration so the parser stays a
/// pure function of its input, which is what lets the fixtures pin it without a container.
/// </summary>
/// <param name="MaxItems">Items in one bundle. A bundle over this is a structural fault, not a truncation.</param>
/// <param name="MaxExcerptLength">Excerpt length in text elements. Short quotation plus a link, not an article.</param>
/// <param name="MaxPlaceNames">Place-name candidates per item.</param>
/// <param name="MaxTitleLength">Title length in text elements.</param>
/// <param name="MaxFieldLength">Length of the short identifying fields: publisher, platform, channel, language.</param>
/// <param name="AllowedPlatforms">
/// Platforms a user-generated item may name. An allow-list rather than a deny-list, so a bundle
/// cannot claim a source from a platform this project has decided it cannot legitimately read.
/// </param>
public sealed record CollectionBundleLimits(
    int MaxItems = 100,
    int MaxExcerptLength = 1000,
    int MaxPlaceNames = 8,
    int MaxTitleLength = 500,
    int MaxFieldLength = 200,
    IReadOnlyCollection<string>? AllowedPlatforms = null)
{
    private static readonly string[] DefaultPlatforms = ["telegram", "bluesky", "mastodon"];

    public IReadOnlyCollection<string> Platforms => AllowedPlatforms ?? DefaultPlatforms;
}
