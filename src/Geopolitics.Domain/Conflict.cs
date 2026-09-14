namespace Geopolitics.Domain;

/// <summary>
/// One armed conflict, as a first-class object rather than as a hard-coded country code.
/// <para>
/// The register of these is <em>discovered</em>. Cataloguing the world's organised violence is the
/// entire job of the projects this system already reads, and their coding names every conflict,
/// names its parties, and says where its events happened. What this class does is hold that, and
/// answer the one question the rest of the system asks of it: does this report belong here.
/// </para>
/// <para>
/// Mutable on purpose, and mutable in exactly one direction. A conflict accumulates the coded events
/// assigned to it, which is how the register learns a conflict's geography from data rather than
/// from a box somebody drew. Nothing else about it changes, because nothing else about it is this
/// system's to change.
/// </para>
/// </summary>
public sealed class Conflict
{
    /// <summary>
    /// How many distinct places one conflict remembers. A cap rather than a target: the register is
    /// built from provider payloads, and the largest real conflict in the 2024 coding is held by
    /// about fifteen hundred names, so this is loose enough never to bite a genuine conflict and
    /// tight enough that a malformed feed cannot grow a row without limit.
    /// </summary>
    public const int MaxPlaceKeys = 4096;

    /// <summary>Countries one conflict is fought in. Generous; the widest in the 2024 coding is nine.</summary>
    public const int MaxCountries = 64;

    public const int MaxNameLength = 200;

    /// <summary>
    /// Event types that can belong to an armed conflict at all.
    /// <para>
    /// Used only to exclude. A protest, a sanctions announcement and a flood are all things this
    /// system tracks and none of them is an event in a war, so admitting them would inflate every
    /// conflict's tempo with reporting about something else. Everything violence-adjacent is
    /// admitted, including <see cref="EventType.Other"/> — an unclassified report is an unknown, and
    /// excluding unknowns would quietly drop most of the text volume.
    /// </para>
    /// <para>
    /// Maritime and naval incidents are in the list for a specific reason: the Houthi campaign
    /// against shipping is coded as one-sided violence by UCDP and reported as a maritime incident by
    /// everybody else, and a taxonomy that put those in different worlds would lose the case this
    /// whole design exists to express.
    /// </para>
    /// </summary>
    private static readonly HashSet<EventType> ViolentTypes =
    [
        EventType.Conflict,
        EventType.Terrorism,
        EventType.MaritimeIncident,
        EventType.NavalIncident,
        EventType.Piracy,
        EventType.MilitaryMovement,
        EventType.CyberIncident,
        EventType.Other,
    ];

    private readonly HashSet<string> placeKeys = new(StringComparer.Ordinal);
    private readonly HashSet<string> countries = new(StringComparer.Ordinal);
    private readonly List<string> actorTokens = [];

    private Conflict(
        string key,
        string name,
        ConflictOrigin origin,
        string? sideA,
        string? sideB,
        string? region,
        string? violence)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            throw new DomainException("A conflict requires a key.");
        }

        if (string.IsNullOrWhiteSpace(name))
        {
            throw new DomainException("A conflict requires a name.");
        }

        Key = key.Trim();
        Name = Cap(name.Trim());
        Origin = origin;
        SideA = Clean(sideA);
        SideB = Clean(sideB);
        Region = Clean(region);
        Violence = Clean(violence);

        foreach (var token in ConflictActorName.Tokenise(SideA).Concat(ConflictActorName.Tokenise(SideB)))
        {
            if (!actorTokens.Contains(token, StringComparer.Ordinal))
            {
                actorTokens.Add(token);
            }
        }
    }

    /// <summary>
    /// A conflict a coding project named. The key carries the project and the project's own
    /// identifier — <c>ucdp:13243</c> — so two projects naming the same war produce two register
    /// entries rather than one entry whose provenance has been lost.
    /// </summary>
    public static Conflict Coded(
        string key,
        string name,
        string? sideA = null,
        string? sideB = null,
        string? region = null,
        string? violence = null) =>
        new(key, name, ConflictOrigin.Coded, sideA, sideB, region, violence);

    /// <summary>
    /// A conflict a model proposed because nothing codes it yet. It is displayed and labelled, and
    /// <see cref="IsAsserted"/> is false until something else supports it.
    /// </summary>
    public static Conflict Proposed(
        string key,
        string name,
        string? sideA = null,
        string? sideB = null,
        string? region = null) =>
        new(key, name, ConflictOrigin.ModelProposed, sideA, sideB, region, violence: null);

    public string Key { get; }

    public string Name { get; }

    public ConflictOrigin Origin { get; }

    /// <summary>The parties, as the coding names them. Null where the coding names none.</summary>
    public string? SideA { get; }

    public string? SideB { get; }

    /// <summary>The coding project's own region label, carried so counts can be grouped as it groups them.</summary>
    public string? Region { get; }

    /// <summary>state-based, non-state or one-sided, where a project codes that distinction.</summary>
    public string? Violence { get; }

    /// <summary>Events the coding has assigned to this conflict.</summary>
    public int CodedEvents { get; private set; }

    /// <summary>Deaths the coding has recorded for it, on its own best estimate.</summary>
    public int CodedDeaths { get; private set; }

    /// <summary>Countries its coded events happened in, resolved from the places rather than stated.</summary>
    public IReadOnlyCollection<string> Countries => countries;

    /// <summary>Normalised names of the places its coded events happened at.</summary>
    public IReadOnlyCollection<string> PlaceKeys => placeKeys;

    /// <summary>
    /// Words identifying the parties. Words the register found in many conflicts are removed from
    /// here as the register is assembled — see <see cref="DropAmbiguousActorToken"/>.
    /// </summary>
    public IReadOnlyList<string> ActorTokens => actorTokens;

    /// <summary>
    /// Whether this conflict may structure anything — carry a tempo, own a narrative, appear as a
    /// category. True for coded conflicts, and false for a model's proposal until something supports
    /// it, which is the same rule a single uncorroborated post is held under.
    /// </summary>
    public bool IsAsserted => Origin == ConflictOrigin.Coded || Corroborated;

    /// <summary>Whether a second, independent source has supported a model-proposed conflict.</summary>
    public bool Corroborated { get; private set; }

    /// <summary>
    /// Records one coded event, or an aggregate of several, against this conflict. This is the only
    /// way a conflict learns its geography: the places are the ones the coding says its events
    /// happened at, and the countries are where those places resolved to.
    /// </summary>
    public void RecordCoded(string? placeName, string? countryCode, int events = 1, int deaths = 0)
    {
        if (events < 0 || deaths < 0)
        {
            throw new DomainException("A conflict cannot record a negative number of events or deaths.");
        }

        CodedEvents += events;
        CodedDeaths += deaths;

        var place = CodedPlaceName.Key(placeName);

        if (place.Length > 0 && placeKeys.Count < MaxPlaceKeys)
        {
            placeKeys.Add(place);
        }

        if (!string.IsNullOrWhiteSpace(countryCode) && countries.Count < MaxCountries)
        {
            countries.Add(countryCode.Trim().ToUpperInvariant());
        }
    }

    /// <summary>
    /// Removes a word that turned out to name conflict itself rather than a party to one.
    /// <para>
    /// Whether a word identifies anybody is a property of the whole register and not of one entry:
    /// "government" and "forces" appear in hundreds of party names and "houthi" appears in one. Only
    /// the assembled register can see that, so it is the assembled register that says so, and the
    /// answer is applied here rather than held alongside — one predicate with one answer beats two
    /// that can disagree.
    /// </para>
    /// </summary>
    public void DropAmbiguousActorToken(string token) => actorTokens.RemoveAll(
        existing => string.Equals(existing, token, StringComparison.Ordinal));

    /// <summary>
    /// Records that a second source supported a model's proposal, which is what lets it stop being a
    /// claim. Has no effect on a coded conflict, which never needed this system's permission.
    /// </summary>
    public void Corroborate() => Corroborated = true;

    /// <summary>
    /// Whether this conflict admits a report, and on what basis. <see cref="ConflictMatchBasis.None"/>
    /// when it does not.
    /// <para>
    /// The bases are tried strongest first and the strongest wins, because membership is one fact and
    /// the interesting part of it is how well supported it is. Geography alone returns
    /// <see cref="ConflictMatchBasis.Country"/>, which callers are expected to treat as a candidate
    /// rather than as a membership: a country with three coded conflicts admits every report from it
    /// to all three, and asserting that would be arithmetic dressed as analysis.
    /// </para>
    /// </summary>
    public ConflictMatchBasis Admits(ConflictCandidate candidate)
    {
        ArgumentNullException.ThrowIfNull(candidate);

        if (!string.IsNullOrWhiteSpace(candidate.CodedConflictKey))
        {
            // A record that states its own conflict is answered by that and nothing else, including
            // when the answer is no. Falling through to a guess about a record that already told us
            // would be overruling the coding with an inference drawn from the coding.
            return string.Equals(candidate.CodedConflictKey.Trim(), Key, StringComparison.OrdinalIgnoreCase)
                ? ConflictMatchBasis.Coded
                : ConflictMatchBasis.None;
        }

        if (!ViolentTypes.Contains(candidate.EventType))
        {
            return ConflictMatchBasis.None;
        }

        if (ActorOverlap(candidate.ActorNames) > 0)
        {
            return ConflictMatchBasis.Actor;
        }

        var country = candidate.CountryCode?.Trim().ToUpperInvariant();
        var inCountry = !string.IsNullOrEmpty(country) && countries.Contains(country);
        var place = CodedPlaceName.Key(candidate.PlaceName);

        // A place name matches only where the country agrees, or where one of the two is unknown.
        // Place names collide across the world constantly — the register holds an Ivorian town and a
        // Saint Lucian one purely because a conflict elsewhere has a place spelled the same — and a
        // bare name match would put a report from Brazil into a war in Lebanon at the strongest
        // geographic basis this system has.
        if (place.Length > 0 && placeKeys.Contains(place) && (inCountry || string.IsNullOrEmpty(country)))
        {
            return ConflictMatchBasis.Place;
        }

        return inCountry ? ConflictMatchBasis.Country : ConflictMatchBasis.None;
    }

    /// <summary>
    /// How many distinct words the report's actors share with the parties to this conflict.
    /// <para>
    /// A count rather than a yes, because it is what separates a real identification from an
    /// incidental one. "Jalisco Cartel New Generation" shares four words with the conflict it names
    /// and one — "cartel" — with every other cartel conflict in the register, and a caller comparing
    /// overlaps gets the right answer without anybody having to decide in advance which words count.
    /// </para>
    /// <para>
    /// Word against word rather than string against string, because "Government of Ukraine" and
    /// "Ukrainian armed forces" are the same party written by two different kinds of writer.
    /// </para>
    /// </summary>
    public int ActorOverlap(IReadOnlyList<string>? actorNames)
    {
        if (actorNames is null || actorNames.Count == 0 || actorTokens.Count == 0)
        {
            return 0;
        }

        var matched = new HashSet<string>(StringComparer.Ordinal);

        foreach (var actor in actorNames)
        {
            foreach (var token in ConflictActorName.Tokenise(actor))
            {
                foreach (var mine in actorTokens)
                {
                    if (ConflictActorName.Same(token, mine))
                    {
                        matched.Add(mine);
                    }
                }
            }
        }

        return matched.Count;
    }

    private static string? Clean(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : Cap(value.Trim());

    private static string Cap(string value) =>
        value.Length <= MaxNameLength ? value : value[..MaxNameLength];
}
