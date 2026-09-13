using Geopolitics.Infrastructure.Location;

namespace Geopolitics.UnitTests;

/// <summary>
/// The index that replaced the linear scan, asserted on its own rather than only through the
/// lexicon.
/// <para>
/// It was written to be behaviour-preserving: the same text and the same spellings must produce the
/// same answer the scan produced, because 544 tests and a published map depended on that answer.
/// These pin the properties that make it so — earliest wins, longest wins at a tie, a match inside a
/// longer word is not a match — against small term sets where the expected answer is obvious by
/// inspection rather than by running the thing being tested.
/// </para>
/// </summary>
public sealed class PlaceNameAutomatonTests
{
    /// <summary>
    /// Terms reach the automaton folded and ordered longest-first, exactly as the lexicon supplies
    /// them, so these read the way the real caller does.
    /// </summary>
    private static PlaceNameAutomaton Build(params string[] terms) =>
        PlaceNameAutomaton.Build([.. terms.OrderByDescending(term => term.Length)]);

    private static string? Find(PlaceNameAutomaton automaton, string[] terms, string text)
    {
        var ordered = terms.OrderByDescending(term => term.Length).ToArray();
        var index = automaton.FindFirst(text);

        return index < 0 ? null : ordered[index];
    }

    [Fact]
    public void TheEarliestMentionWinsRatherThanTheLongestOrTheFirstListed()
    {
        string[] terms = ["kharkiv", "odesa"];

        // Reporting states where something happened before it lists what reacted to it, so position
        // in the text is the signal — not which term the lexicon happens to hold first.
        Assert.Equal("odesa", Find(Build(terms), terms, "a strike on odesa drew a response from kharkiv"));
    }

    [Fact]
    public void ALongerNameWinsOverTheShorterOneInsideItAtTheSamePosition()
    {
        string[] terms = ["strait of hormuz", "hormuz"];

        // Both match, and the shorter one ends first. Stopping at the first match found would return
        // "hormuz" and lose the strait.
        Assert.Equal("strait of hormuz", Find(Build(terms), terms, "traffic through the strait of hormuz fell"));
    }

    [Fact]
    public void ALongerNameStartingEarlierWinsEvenThoughItEndsLater()
    {
        string[] terms = ["port sudan", "sudan"];

        // The hard case for a scan that reports matches as they end: "sudan" completes first, while
        // "port sudan" starts earlier and is the right answer.
        Assert.Equal("port sudan", Find(Build(terms), terms, "unrest in port sudan continued"));
    }

    [Fact]
    public void AMatchInsideALongerWordIsNotAMention()
    {
        string[] terms = ["us", "united states"];

        // "us" occurs in "because" and in "thus" before the real mention. Taking the earliest match
        // without a word-boundary check would return one of those and outrank everything after it.
        Assert.Equal("united states", Find(Build(terms), terms, "because of this, and thus, the united states replied"));
    }

    [Fact]
    public void AShortTermStillMatchesWhenItStandsAsAWord()
    {
        string[] terms = ["us"];

        Assert.Equal("us", Find(Build(terms), terms, "the us said little"));
    }

    [Fact]
    public void AScriptWrittenWithoutWordBreaksMatchesWithNoWordEdgeToFind()
    {
        string[] terms = ["美国"];

        // Requiring a word edge here would mean never matching at all, since that is how the language
        // is written.
        Assert.Equal("美国", Find(Build(terms), terms, "在美国发生"));
    }

    [Fact]
    public void TextNamingNothingReturnsNoMatch()
    {
        Assert.Equal(-1, Build("odesa", "kharkiv").FindFirst("a report that names nowhere in particular"));
    }

    [Fact]
    public void AnEmptyTermSetMatchesNothingRatherThanThrowing()
    {
        Assert.Equal(-1, PlaceNameAutomaton.Build([]).FindFirst("anything at all"));
        Assert.Equal(-1, PlaceNameAutomaton.Build([]).FindFirst(string.Empty));
    }

    [Fact]
    public void TheFirstOfTwoIdenticalTermsWins()
    {
        // Two spellings can fold to one string. The caller's order decides which entry the match
        // belongs to, and it must be the first, because that is what the scan it replaced did.
        Assert.Equal(0, PlaceNameAutomaton.Build(["aden", "aden"]).FindFirst("fighting near aden"));
    }

    [Fact]
    public void AMentionAtTheVeryStartAndAtTheVeryEndAreBothFound()
    {
        string[] terms = ["aden"];

        Assert.Equal("aden", Find(Build(terms), terms, "aden was quiet"));
        Assert.Equal("aden", Find(Build(terms), terms, "the vessel sailed for aden"));
    }

    [Fact]
    public void EveryDistinctTermMentionedIsCollected()
    {
        var terms = new[] { "odesa", "kharkiv", "kyiv" };
        var ordered = terms.OrderByDescending(term => term.Length).ToArray();
        var found = Build(terms)
            .FindAll("odesa and kharkiv and odesa again and kyiv", 10)
            .Select(index => ordered[index])
            .ToHashSet();

        // Distinct, because a report naming Odesa twice is not two pieces of evidence about which
        // country it is describing.
        Assert.Equal(["odesa", "kharkiv", "kyiv"], found);
    }

    [Fact]
    public void CollectingMentionsStopsAtTheLimit()
    {
        var automaton = Build("odesa", "kharkiv", "kyiv");

        Assert.Equal(2, automaton.FindAll("odesa kharkiv kyiv", 2).Count);
        Assert.Empty(automaton.FindAll("odesa kharkiv kyiv", 0));
    }

    [Fact]
    public void CollectingMentionsAppliesTheSameWordBoundaryRule()
    {
        var automaton = Build("us");

        // Same rule as the single-mention path. A context signal drawn from "because" would be worse
        // than no context signal at all.
        Assert.Empty(automaton.FindAll("because of thus and thus", 10));
        Assert.Single(automaton.FindAll("the us replied", 10));
    }

    [Fact]
    public void ManyTermsSharingPrefixesAllRemainFindable()
    {
        // The trie is built by appending in sorted order, which is only correct if a shared prefix
        // descends into the node that already exists. A bug there loses whole branches silently, so
        // this asserts every term is still found rather than sampling one.
        var terms = Enumerable.Range(0, 200).Select(index => $"alphaville{index:D3}").ToArray();
        var automaton = PlaceNameAutomaton.Build(terms);

        for (var index = 0; index < terms.Length; index++)
        {
            Assert.Equal(index, automaton.FindFirst($"reporting from {terms[index]} today"));
        }
    }
}
