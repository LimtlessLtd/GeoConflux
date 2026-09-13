using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Geopolitics.Infrastructure.Sources.Providers;

namespace Geopolitics.Infrastructure.Sources.Briefs;

/// <summary>
/// Reads a collection bundle, treating it as untrusted input.
/// <para>
/// No allowance is made for a bundle having been produced by an agent working on this project. The
/// producer was asked not to supply coordinates; validation runs regardless, because instruction is
/// the part a hostile document can argue with. A page a collector reads may carry text aimed at the
/// collector itself, and the defence against a collector that was successfully redirected is not a
/// better instruction — it is that the schema has no field for a coordinate, the caps are enforced
/// here, and a cited document that was never fetched still has to produce a hash.
/// </para>
/// <para>
/// A pure function of its input, like every other parser in this project: a string and a set of
/// limits go in, records come out, and there is no HTTP, no clock of its own, and no container.
/// </para>
/// </summary>
public static class CollectionBundleParser
{
    /// <summary>The only schema version this build understands.</summary>
    public const int SupportedSchemaVersion = 1;

    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web)
    {
        // A property nobody mapped is a bundle written against a different contract. Reading it
        // best-effort would silently drop whatever that property was carrying.
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        ReadCommentHandling = JsonCommentHandling.Skip,
    };

    /// <summary>
    /// Parses and validates one bundle.
    /// </summary>
    /// <param name="document">Raw file contents.</param>
    /// <param name="now">Current time, supplied by the caller so the parser has no clock of its own.</param>
    /// <param name="limits">Caps to enforce.</param>
    /// <exception cref="FormatException">
    /// The bundle is structurally wrong: unreadable, a schema version this build does not know, or
    /// missing something every bundle must have. A structural fault rejects the whole file, because
    /// the alternative is guessing what the author meant.
    /// </exception>
    public static CollectionBundleReadResult Parse(string document, DateTimeOffset now, CollectionBundleLimits? limits = null)
    {
        limits ??= new CollectionBundleLimits();

        if (string.IsNullOrWhiteSpace(document))
        {
            throw new FormatException("The bundle was empty.");
        }

        BundleDocument? wire;

        try
        {
            wire = JsonSerializer.Deserialize<BundleDocument>(document, SerializerOptions);
        }
        catch (JsonException exception)
        {
            throw new FormatException($"The bundle is not readable JSON: {exception.Message}", exception);
        }

        if (wire is null)
        {
            throw new FormatException("The bundle deserialised to nothing.");
        }

        if (wire.SchemaVersion != SupportedSchemaVersion)
        {
            throw new FormatException(
                $"Bundle schema version {wire.SchemaVersion} is not supported; this build reads version {SupportedSchemaVersion}.");
        }

        var bundleId = Sanitise(wire.BundleId, limits.MaxFieldLength);

        if (string.IsNullOrEmpty(bundleId))
        {
            throw new FormatException("The bundle has no bundleId.");
        }

        if (wire.CollectedAt > now)
        {
            throw new FormatException($"The bundle claims to have been collected at {wire.CollectedAt:O}, which is in the future.");
        }

        if (wire.Brief is null)
        {
            throw new FormatException("The bundle names no brief.");
        }

        var briefId = Sanitise(wire.Brief.Id, limits.MaxFieldLength);

        if (string.IsNullOrEmpty(briefId))
        {
            throw new FormatException("The bundle's brief has no id.");
        }

        var items = wire.Items ?? [];

        if (items.Count > limits.MaxItems)
        {
            // Truncating would publish a partial run while reporting a whole one. A bundle over the
            // cap is a collector that ignored its brief, and that is worth failing loudly.
            throw new FormatException($"The bundle carries {items.Count} items, above the limit of {limits.MaxItems}.");
        }

        var accepted = new List<CollectedItem>(items.Count);
        var rejected = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        for (var index = 0; index < items.Count; index++)
        {
            var outcome = ReadItem(items[index], now, limits);

            if (outcome.Item is null)
            {
                rejected.Add($"item {index}: {outcome.Reason}");
                continue;
            }

            if (!seen.Add(outcome.Item.Url.AbsoluteUri))
            {
                rejected.Add($"item {index}: {outcome.Item.Url} appears more than once in this bundle.");
                continue;
            }

            accepted.Add(outcome.Item);
        }

        return new CollectionBundleReadResult(
            new CollectionBundle(
                bundleId,
                wire.CollectedAt,
                briefId,
                wire.Brief.Revision,
                accepted,
                ReadCoverage(wire.Coverage, limits)),
            rejected);
    }

    /// <summary>
    /// The run's account of what each source gave, treated as untrusted like everything else here.
    /// <para>
    /// Absent is fine and means a run that predates the block, not a run that reached nothing. The
    /// two would be worth distinguishing if anything acted on this; nothing does — it is displayed,
    /// and a displayed figure that is missing is visibly missing.
    /// </para>
    /// </summary>
    private static List<CollectedSourceOutcome> ReadCoverage(BundleCoverage? coverage, CollectionBundleLimits limits)
    {
        if (coverage?.Sources is not { Count: > 0 } sources)
        {
            return [];
        }

        var outcomes = new List<CollectedSourceOutcome>(sources.Count);

        foreach (var source in sources.Take(limits.MaxItems))
        {
            var channel = Sanitise(source.Channel, limits.MaxFieldLength);
            var outcome = Sanitise(source.Outcome, limits.MaxFieldLength);

            if (string.IsNullOrEmpty(channel) || string.IsNullOrEmpty(outcome))
            {
                continue;
            }

            outcomes.Add(new CollectedSourceOutcome(
                channel,
                outcome,
                NullIfEmpty(Sanitise(source.Reason, limits.MaxFieldLength)),
                Math.Max(0, source.Read),
                Math.Max(0, source.Matched),
                Math.Max(0, source.Collected)));
        }

        return outcomes;
    }

    private static (CollectedItem? Item, string Reason) ReadItem(BundleItem item, DateTimeOffset now, CollectionBundleLimits limits)
    {
        if (!TryReadKind(item.Kind, out var kind))
        {
            return (null, $"'{item.Kind}' is not a recognised item kind.");
        }

        if (!TryReadUrl(item.Url, out var url, out var urlProblem))
        {
            return (null, urlProblem);
        }

        var publisher = Sanitise(item.Publisher, limits.MaxFieldLength);
        var platform = Sanitise(item.Platform, limits.MaxFieldLength).ToLowerInvariant();
        var channel = Sanitise(item.Channel, limits.MaxFieldLength);

        if (kind == CollectedItemKind.Document && string.IsNullOrEmpty(publisher))
        {
            return (null, "a document item names no publisher.");
        }

        if (kind == CollectedItemKind.UserGenerated)
        {
            if (string.IsNullOrEmpty(platform) || string.IsNullOrEmpty(channel))
            {
                return (null, "a user-generated item names no platform and channel.");
            }

            if (!limits.Platforms.Contains(platform, StringComparer.OrdinalIgnoreCase))
            {
                // A platform this project cannot legitimately read must not be nameable as a source,
                // whatever a bundle claims.
                return (null, $"'{platform}' is not an allowed platform.");
            }
        }

        // postedAt and publishedAt are the same fact under two names, because a wire "publishes" and
        // an account "posts". Accepting either keeps the bundle readable to whoever writes it.
        var publishedAt = item.PublishedAt ?? item.PostedAt;

        if (publishedAt is not { } published)
        {
            return (null, "the item states no publication time.");
        }

        if (published > now)
        {
            return (null, $"it claims to have been published at {published:O}, which is in the future.");
        }

        if (item.RetrievedAt > now)
        {
            return (null, $"it claims to have been retrieved at {item.RetrievedAt:O}, which is in the future.");
        }

        if (item.RetrievedAt < published)
        {
            return (null, "it claims to have been retrieved before it was published.");
        }

        var contentHash = Sanitise(item.ContentHash, limits.MaxFieldLength);

        if (!IsSha256(contentHash))
        {
            return (null, "the content hash is missing or is not a sha256 digest.");
        }

        if (item.ExcerptTranslation is not null)
        {
            // The collector quotes and the enrichment stage translates. A collector-supplied
            // translation is an unvalidated paraphrase in the one field the citation rests on.
            return (null, "the item carries a collector-supplied translation.");
        }

        var excerpt = Sanitise(item.Excerpt, limits.MaxExcerptLength);

        if (string.IsNullOrEmpty(excerpt))
        {
            return (null, "the item quotes nothing.");
        }

        if (CountTextElements(excerpt) > limits.MaxExcerptLength)
        {
            return (null, $"the excerpt is longer than {limits.MaxExcerptLength} characters.");
        }

        var placeNames = (item.PlaceNames ?? [])
            .Select(name => Sanitise(name, limits.MaxFieldLength))
            .Where(name => !string.IsNullOrEmpty(name))
            .ToArray();

        if (placeNames.Length > limits.MaxPlaceNames)
        {
            return (null, $"the item names {placeNames.Length} places, above the limit of {limits.MaxPlaceNames}.");
        }

        var relatedTo = (item.RelatedTo ?? [])
            .Where(related => TryReadUrl(related, out _, out _))
            .Take(limits.MaxPlaceNames)
            .ToArray();

        return (
            new CollectedItem(
                kind,
                url,
                kind == CollectedItemKind.Document ? publisher : null,
                kind == CollectedItemKind.UserGenerated ? platform : null,
                kind == CollectedItemKind.UserGenerated ? channel : null,
                NullIfEmpty(Sanitise(item.Title, limits.MaxTitleLength)),
                published,
                item.RetrievedAt,
                contentHash,
                NullIfEmpty(Sanitise(item.Language, limits.MaxFieldLength)),
                excerpt,
                placeNames,
                relatedTo),
            string.Empty);
    }

    private static bool TryReadKind(string? value, out CollectedItemKind kind)
    {
        kind = CollectedItemKind.Document;

        return value?.Trim().ToLowerInvariant() switch
        {
            "document" => true,
            "usergenerated" => Assign(CollectedItemKind.UserGenerated, out kind),
            _ => false,
        };

        static bool Assign(CollectedItemKind value, out CollectedItemKind target)
        {
            target = value;
            return true;
        }
    }

    /// <summary>
    /// Accepts an absolute <c>http</c> or <c>https</c> URL that does not point somewhere an outbound
    /// poll may not go.
    /// <para>
    /// A literal address is checked here against the same policy the HTTP stack enforces. A hostname
    /// is not, because resolving it would need DNS and this is a pure function — the connection-time
    /// screen in ADR 021 is what covers a name that resolves inward, and it covers it at the only
    /// place where the answer cannot change afterwards.
    /// </para>
    /// </summary>
    private static bool TryReadUrl(string? value, out Uri url, out string problem)
    {
        url = null!;
        problem = string.Empty;

        if (string.IsNullOrWhiteSpace(value))
        {
            problem = "the item cites no URL.";
            return false;
        }

        if (!Uri.TryCreate(value.Trim(), UriKind.Absolute, out var parsed))
        {
            problem = "the cited URL is not absolute.";
            return false;
        }

        if (parsed.Scheme != Uri.UriSchemeHttp && parsed.Scheme != Uri.UriSchemeHttps)
        {
            problem = $"'{parsed.Scheme}' is not a URL scheme this reads.";
            return false;
        }

        if (IPAddress.TryParse(parsed.Host, out var literal) && !OutboundAddressPolicy.IsAllowed(literal))
        {
            problem = "the cited URL points into an address range an outbound poll may not reach.";
            return false;
        }

        url = parsed;
        return true;
    }

    private static bool IsSha256(string value) =>
        value.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase)
        && value.Length == 71
        && value.AsSpan(7).ContainsOnlyHex();

    /// <summary>
    /// Normalises one untrusted string: NFC, control characters and bidi overrides removed,
    /// whitespace collapsed, trimmed, and hard-capped well above the field's own limit.
    /// <para>
    /// NFC rather than NFKC, which is the opposite of what the gazetteer does when it searches, and
    /// deliberately so. This text is stored and shown to a reader, so it should stay as written;
    /// the gazetteer is matching, so it folds aggressively. Using one form for both jobs would
    /// either corrupt the quotation or fail the lookup.
    /// </para>
    /// <para>
    /// Control characters go. Bidi overrides and isolates go too, because they can make a string
    /// render as something other than what it says, which is a real problem in a field quoting
    /// right-to-left text. Other format characters stay: the zero-width non-joiner is a
    /// letter-affecting character in Persian, and stripping it corrupts ordinary words.
    /// </para>
    /// </summary>
    private static string Sanitise(string? value, int cap)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        string normalised;

        try
        {
            normalised = value.Normalize(NormalizationForm.FormC);
        }
        catch (ArgumentException)
        {
            // Malformed Unicode is not a reason to lose the item; it is a reason not to normalise it.
            normalised = value;
        }

        var builder = new StringBuilder(normalised.Length);
        var lastWasSpace = false;

        foreach (var character in normalised)
        {
            if (char.IsControl(character) || char.IsWhiteSpace(character))
            {
                if (!lastWasSpace && builder.Length > 0)
                {
                    builder.Append(' ');
                    lastWasSpace = true;
                }

                continue;
            }

            if (IsBidiOverride(character))
            {
                continue;
            }

            builder.Append(character);
            lastWasSpace = false;
        }

        var result = builder.ToString().TrimEnd();

        // A generous hard stop so a caller that forgot to check a length cannot be handed megabytes.
        // The field's own limit is enforced by the caller, in text elements, which is the figure a
        // person would recognise.
        var ceiling = Math.Max(cap, 1) * 4;

        return result.Length <= ceiling ? result : result[..ceiling];
    }

    /// <summary>
    /// Bidi embeddings, overrides and isolates: U+202A-U+202E and U+2066-U+2069.
    /// <para>
    /// Written as escapes rather than as themselves, because as themselves they are invisible and
    /// would reorder the rendering of this very file — which is the property that makes them worth
    /// removing from a field that quotes right-to-left text.
    /// </para>
    /// </summary>
    private static bool IsBidiOverride(char character) =>
        character is (>= (char)0x202A and <= (char)0x202E) or (>= (char)0x2066 and <= (char)0x2069);

    /// <summary>
    /// Length as a reader would count it. A cap written for English measured in UTF-16 units would
    /// cut Arabic and Han text at a fraction of its stated limit, and could cut it mid-character.
    /// </summary>
    private static int CountTextElements(string value) => new StringInfo(value).LengthInTextElements;

    private static string? NullIfEmpty(string value) => string.IsNullOrEmpty(value) ? null : value;

    private static bool ContainsOnlyHex(this ReadOnlySpan<char> value)
    {
        foreach (var character in value)
        {
            if (!char.IsAsciiHexDigit(character))
            {
                return false;
            }
        }

        return true;
    }

    private sealed record BundleDocument
    {
        public int SchemaVersion { get; init; }

        public string? BundleId { get; init; }

        public DateTimeOffset CollectedAt { get; init; }

        public BundleBrief? Brief { get; init; }

        public BundleCollector? Collector { get; init; }

        public IReadOnlyList<BundleItem>? Items { get; init; }

        public BundleCoverage? Coverage { get; init; }
    }

    /// <summary>The run's account of what each source it asked actually gave.</summary>
    private sealed record BundleCoverage
    {
        public IReadOnlyList<BundleSourceOutcome>? Sources { get; init; }
    }

    private sealed record BundleSourceOutcome
    {
        public string? Channel { get; init; }

        public string? Outcome { get; init; }

        public string? Reason { get; init; }

        public int Read { get; init; }

        public int Matched { get; init; }

        public int Collected { get; init; }
    }

    private sealed record BundleBrief
    {
        public string? Id { get; init; }

        public int Revision { get; init; }

        public DateTimeOffset? WindowFrom { get; init; }

        public DateTimeOffset? WindowTo { get; init; }
    }

    /// <summary>
    /// Who ran the collection. Recorded as provenance of the data, and deliberately vendor-neutral:
    /// no part of this system should depend on which collector produced a run.
    /// </summary>
    private sealed record BundleCollector
    {
        public string? Role { get; init; }

        public string? RunId { get; init; }
    }

    private sealed record BundleItem
    {
        public string? Kind { get; init; }

        public string? Url { get; init; }

        public string? Publisher { get; init; }

        public string? Platform { get; init; }

        public string? Channel { get; init; }

        public string? Title { get; init; }

        public DateTimeOffset? PublishedAt { get; init; }

        public DateTimeOffset? PostedAt { get; init; }

        public DateTimeOffset RetrievedAt { get; init; }

        public string? ContentHash { get; init; }

        public string? Language { get; init; }

        public string? Excerpt { get; init; }

        /// <summary>
        /// Must stay null. It exists in the contract only so that a bundle carrying one is rejected
        /// with a stated reason rather than failing as an unmapped property.
        /// </summary>
        public string? ExcerptTranslation { get; init; }

        public IReadOnlyList<string>? PlaceNames { get; init; }

        public IReadOnlyList<string>? RelatedTo { get; init; }
    }
}
