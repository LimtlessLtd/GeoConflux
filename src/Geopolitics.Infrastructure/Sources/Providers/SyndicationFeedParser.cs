using System.Globalization;
using System.Net;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;

namespace Geopolitics.Infrastructure.Sources.Providers;

/// <param name="Identifier">Stable per-item identifier from the feed: a GUID, an Atom id, or the link.</param>
/// <param name="PublishedAt">When the publisher says the item was published, when it says at all.</param>
/// <param name="Language">
/// The language the feed declares for itself, as a BCP 47 tag, or null when it declares none.
/// <para>
/// Read because the alternative is worse than it looks. Language is otherwise determined from the
/// script, which is all that can be established without a model — and a script tells Arabic from
/// Cyrillic while telling nothing at all between French, Spanish and English. A feed stating it
/// publishes in French is the publisher's own declaration and beats this system's inference, which
/// is the hierarchy every other Declared field here follows.
/// </para>
/// </param>
public sealed record SyndicationEntry(
    string? Identifier,
    string Title,
    string Summary,
    DateTimeOffset? PublishedAt,
    string? Language = null);

/// <summary>
/// Turns an RSS 2.0 or Atom document into entries, with no knowledge of HTTP or of the pipeline.
/// <para>
/// Kept as a pure function of a string so the awkward cases that actually occur in the wild can be
/// covered by fixture tests: namespaced and non-namespaced feeds, HTML-laden descriptions, missing
/// dates, and malformed XML. It is also a parser of untrusted input, so entity and DTD processing are
/// switched off rather than left at their defaults.
/// </para>
/// </summary>
public static partial class SyndicationFeedParser
{
    /// <summary>
    /// Cap on a single summary. Publishers sometimes put an entire article in a description, and
    /// downstream enrichment is charged by the token.
    /// </summary>
    private const int MaxSummaryLength = 4_000;

    private const int MaxTitleLength = 400;

    private static readonly XNamespace AtomNamespace = "http://www.w3.org/2005/Atom";

    private static readonly XmlReaderSettings ReaderSettings = new()
    {
        // A syndication feed is attacker-influenced input. Prohibiting DTDs removes external entity
        // expansion and the entity-expansion denial of service in one step; neither is needed to
        // read RSS or Atom.
        DtdProcessing = DtdProcessing.Prohibit,
        XmlResolver = null,
        MaxCharactersFromEntities = 1_024,
        IgnoreComments = true,
        IgnoreProcessingInstructions = true,
        IgnoreWhitespace = true,
    };

    /// <summary>
    /// Parses a feed document.
    /// </summary>
    /// <exception cref="FormatException">The payload is not well-formed XML or is not a feed.</exception>
    public static IReadOnlyList<SyndicationEntry> Parse(string document)
    {
        if (string.IsNullOrWhiteSpace(document))
        {
            throw new FormatException("The feed response was empty.");
        }

        XDocument parsed;

        try
        {
            using var stringReader = new StringReader(document);
            using var reader = XmlReader.Create(stringReader, ReaderSettings);
            parsed = XDocument.Load(reader);
        }
        catch (XmlException exception)
        {
            // Translated on purpose: callers handle "this provider sent us rubbish", and should not
            // have to know that the rubbish happened to arrive as XML.
            throw new FormatException($"The feed response was not well-formed XML: {exception.Message}", exception);
        }

        var root = parsed.Root ?? throw new FormatException("The feed response had no root element.");

        return root.Name.LocalName switch
        {
            "rss" or "rdf" or "RDF" => ParseRss(root),
            "feed" => ParseAtom(root),
            _ => throw new FormatException($"Unrecognised feed root element '{root.Name.LocalName}'."),
        };
    }

    private static List<SyndicationEntry> ParseRss(XElement root)
    {
        // Descendants rather than a fixed path: RSS 1.0 puts items beside the channel, RSS 2.0 puts
        // them inside it, and both are served as "RSS" by real publishers.
        var items = root.Descendants().Where(element => element.Name.LocalName == "item");
        var entries = new List<SyndicationEntry>();

        // Declared once on the channel and inherited by every item, which is how RSS expresses it.
        // An item that overrides it wins below.
        var feedLanguage = Language(root.Descendants()
            .FirstOrDefault(element => element.Name.LocalName == "channel") ?? root);

        foreach (var item in items)
        {
            var title = Clean(Value(item, "title"), MaxTitleLength);
            var summary = Clean(Value(item, "description") ?? Value(item, "encoded") ?? Value(item, "summary"), MaxSummaryLength);

            if (string.IsNullOrWhiteSpace(title) && string.IsNullOrWhiteSpace(summary))
            {
                continue;
            }

            entries.Add(new SyndicationEntry(
                Value(item, "guid") ?? Value(item, "link"),
                title.Length == 0 ? Truncate(summary, MaxTitleLength) : title,
                summary.Length == 0 ? title : summary,
                ParseDate(Value(item, "pubDate") ?? Value(item, "date")),
                Language(item) ?? feedLanguage));
        }

        return entries;
    }

    private static List<SyndicationEntry> ParseAtom(XElement root)
    {
        var entries = new List<SyndicationEntry>();

        foreach (var entry in root.Elements(AtomNamespace + "entry").Concat(root.Elements("entry")))
        {
            var title = Clean(Value(entry, "title"), MaxTitleLength);
            var summary = Clean(Value(entry, "summary") ?? Value(entry, "content"), MaxSummaryLength);

            if (string.IsNullOrWhiteSpace(title) && string.IsNullOrWhiteSpace(summary))
            {
                continue;
            }

            entries.Add(new SyndicationEntry(
                Value(entry, "id") ?? LinkHref(entry),
                title.Length == 0 ? Truncate(summary, MaxTitleLength) : title,
                summary.Length == 0 ? title : summary,
                ParseDate(Value(entry, "published") ?? Value(entry, "updated"))));
        }

        return entries;
    }

    /// <summary>
    /// Reads the first child with this local name, ignoring namespace. Feeds mix Dublin Core,
    /// content, and bare elements freely, and matching on local name is what makes one code path
    /// work across them.
    /// </summary>
    /// <summary>
    /// The language an element declares, from either the RSS element or the XML attribute.
    /// </summary>
    /// <remarks>
    /// Normalised to its primary subtag — <c>fr-FR</c> becomes <c>fr</c> — because what the coverage
    /// panel counts is languages rather than locales, and leaving both would report French twice for
    /// two publishers who happened to write the tag differently.
    /// </remarks>
    private static string? Language(XElement element)
    {
        var declared = Value(element, "language")
            ?? element.Attributes().FirstOrDefault(attribute => attribute.Name.LocalName == "lang")?.Value;

        if (string.IsNullOrWhiteSpace(declared))
        {
            return null;
        }

        var subtags = declared.Trim().Split('-', StringSplitOptions.RemoveEmptyEntries);

        // The whole value has to look like a tag before its first part is trusted, not just the
        // first part on its own. "not-a-language-tag-at-all-really" has a plausible three-letter
        // head and is plainly not a language, and reading it as "not" would put a language nobody
        // publishes in onto the coverage panel — which is a worse outcome than reading nothing,
        // because it would be counted rather than shown as unknown.
        //
        // A language subtag is two or three letters; the rest are script, region or variant, and a
        // real tag has few of them. This accepts fr, fr-FR and zh-Hant-TW and rejects a sentence.
        var wellFormed = subtags.Length is >= 1 and <= 3
            && subtags[0].Length is 2 or 3
            && subtags.All(subtag => subtag.Length is >= 2 and <= 8 && subtag.All(char.IsAsciiLetterOrDigit));

        return wellFormed ? subtags[0].ToLowerInvariant() : null;
    }

    private static string? Value(XElement parent, string localName)
    {
        var match = parent.Elements().FirstOrDefault(element =>
            string.Equals(element.Name.LocalName, localName, StringComparison.OrdinalIgnoreCase));

        var value = match?.Value.Trim();
        return string.IsNullOrWhiteSpace(value) ? null : value;
    }

    private static string? LinkHref(XElement entry) => entry
        .Elements()
        .Where(element => string.Equals(element.Name.LocalName, "link", StringComparison.OrdinalIgnoreCase))
        .Select(element => element.Attribute("href")?.Value)
        .FirstOrDefault(href => !string.IsNullOrWhiteSpace(href));

    /// <summary>
    /// Reduces publisher markup to the text a reader and an enrichment model both need. Publishers
    /// routinely put tracking pixels, share widgets, and escaped HTML into a description, none of
    /// which carries meaning and all of which costs tokens.
    /// </summary>
    private static string Clean(string? value, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        // Decoded before tags are stripped: a description often arrives as escaped HTML, so the
        // markup is not visible as markup until this step has run.
        var decoded = WebUtility.HtmlDecode(value);
        var stripped = HtmlTag().Replace(decoded, " ");
        var collapsed = Whitespace().Replace(stripped, " ").Trim();

        return Truncate(collapsed, maxLength);
    }

    private static string Truncate(string value, int maxLength) =>
        value.Length <= maxLength ? value : string.Concat(value.AsSpan(0, maxLength - 1), "…");

    /// <summary>
    /// RFC 822 layouts, which is what an RSS <c>pubDate</c> uses. Both two- and four-digit days occur,
    /// seconds are optional, and the zone arrives either as an offset or as a name.
    /// </summary>
    private static readonly string[] Rfc822Formats =
    [
        "dd MMM yyyy HH:mm:ss zzz",
        "d MMM yyyy HH:mm:ss zzz",
        "dd MMM yyyy HH:mm zzz",
        "d MMM yyyy HH:mm zzz",
        "dd MMM yyyy HH:mm:ss 'GMT'",
        "d MMM yyyy HH:mm:ss 'GMT'",
        "dd MMM yyyy HH:mm:ss 'UT'",
        "dd MMM yyyy HH:mm:ss 'Z'",
    ];

    /// <summary>
    /// Parses whichever date format the publisher used. An unreadable or absent date is not an
    /// error: the pipeline falls back to arrival time, which is a weaker but honest claim.
    /// <para>
    /// The leading weekday is discarded before parsing rather than matched. .NET validates a day name
    /// against the date and rejects the value when they disagree, and feeds in the wild do publish
    /// dates whose weekday is wrong. The day name is redundant information, so a mismatch is a reason
    /// to ignore the name, not a reason to throw away a perfectly good timestamp.
    /// </para>
    /// </summary>
    private static DateTimeOffset? ParseDate(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        const DateTimeStyles Styles = DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal;
        var trimmed = value.Trim();

        // ISO 8601, which Atom mandates and which many RSS feeds use through Dublin Core.
        if (DateTimeOffset.TryParse(trimmed, CultureInfo.InvariantCulture, Styles, out var parsed))
        {
            return parsed;
        }

        var comma = trimmed.IndexOf(',', StringComparison.Ordinal);
        var withoutWeekday = comma >= 0 ? trimmed[(comma + 1)..].Trim() : trimmed;

        return DateTimeOffset.TryParseExact(
            withoutWeekday,
            Rfc822Formats,
            CultureInfo.InvariantCulture,
            Styles,
            out var rfc822)
            ? rfc822
            : null;
    }

    [GeneratedRegex("<[^>]{0,2000}>", RegexOptions.None, matchTimeoutMilliseconds: 1_000)]
    private static partial Regex HtmlTag();

    [GeneratedRegex(@"\s+", RegexOptions.None, matchTimeoutMilliseconds: 1_000)]
    private static partial Regex Whitespace();
}
