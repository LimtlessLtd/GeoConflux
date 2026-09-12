using Geopolitics.Infrastructure.Location;

namespace Geopolitics.UnitTests;

public sealed class GazetteerTests
{
    [Fact]
    public void AShortAliasIsNotFoundInsideAnUnrelatedWord()
    {
        // "US" is an alias for the United States. Searched as a bare substring it also appears in
        // "because", "thus", and "Russia" — and because the search takes the EARLIEST match across
        // every term, one such hit outranks the real place name later in the sentence.
        var place = Gazetteer.FindFirstMention("Because of the strike, shipping avoided Bab-el-Mandeb.");

        Assert.Equal("Bab-el-Mandeb", place);
    }

    [Theory]
    [InlineData("The US said it would respond.", "United States")]
    [InlineData("Reported in the UK overnight.", "United Kingdom")]
    [InlineData("A convoy left Odesa at dawn.", "Odesa")]
    public void AWholeWordMentionIsStillFound(string text, string expected) =>
        Assert.Equal(expected, Gazetteer.FindFirstMention(text));

    [Theory]
    // Arabic
    [InlineData("باب المندب", "Bab-el-Mandeb")]
    [InlineData("صنعاء", "Sanaa")]
    [InlineData("القاهرة", "Cairo")]
    // Persian, which writes ک and ی where Arabic writes ك and ي
    [InlineData("خلیج فارس", "Persian Gulf")]
    [InlineData("تهران", "Tehran")]
    // Cyrillic, Ukrainian and Russian spellings of the same city
    [InlineData("Київ", "Kyiv")]
    [InlineData("Киев", "Kyiv")]
    [InlineData("Харків", "Kharkiv")]
    [InlineData("Харьков", "Kharkiv")]
    [InlineData("Чёрное море", "Black Sea")]
    // Han, simplified and traditional
    [InlineData("台湾海峡", "Taiwan Strait")]
    [InlineData("臺灣海峽", "Taiwan Strait")]
    [InlineData("北京", "Beijing")]
    [InlineData("苏伊士运河", "Suez Canal")]
    // Hangul and Devanagari
    [InlineData("서울", "Seoul")]
    [InlineData("भारत", "India")]
    public void ANativeScriptNameResolvesToItsCanonicalEntry(string name, string expected)
    {
        Assert.True(Gazetteer.TryResolve(name, out var entry), $"'{name}' did not resolve.");
        Assert.Equal(expected, entry.CanonicalName);
    }

    [Theory]
    // Arabic and Cyrillic space their words, so the ordinary boundary rule applies.
    [InlineData("انفجار في باب المندب اليوم", "Bab-el-Mandeb")]
    [InlineData("Вчера в Одесса произошёл взрыв", "Odesa")]
    // Han and Hangul do not. Requiring a word edge would mean never matching them at all.
    [InlineData("今天在台湾海峡发生了事件", "Taiwan Strait")]
    [InlineData("서울에서 발생한 사건", "Seoul")]
    public void ANativeScriptNameIsFoundInsideProse(string text, string expected) =>
        Assert.Equal(expected, Gazetteer.FindFirstMention(text));

    [Fact]
    public void ALongerNameWinsOverTheShorterOneItContains()
    {
        // "Taiwan" is an alias in its own right and sits inside "Taiwan Strait".
        Assert.Equal("Taiwan Strait", Gazetteer.FindFirstMention("Transits of the Taiwan Strait rose."));
        Assert.Equal("Taiwan Strait", Gazetteer.FindFirstMention("台湾海峡的通行量上升"));
    }

    [Fact]
    public void TextNamingNoKnownPlaceResolvesToNothing() =>
        Assert.Null(Gazetteer.FindFirstMention("A quiet day with nothing to report."));

    [Fact]
    public void TheLexiconBuildsWithoutColliding()
    {
        // BuildLookup throws when two aliases normalise to one key while denoting different places.
        // Touching it here means a bad alias fails as a named assertion rather than as a
        // TypeInitializationException from whichever unrelated test happened to run first.
        var exception = Record.Exception(() => Gazetteer.TryResolve("Kyiv", out _));

        Assert.Null(exception);
    }

    [Fact]
    public void ContestedSpellingsResolveToTheSamePlace()
    {
        // Neither spelling is a mistake, and the gazetteer's job is to place the report, not to
        // adjudicate the name.
        Assert.True(Gazetteer.TryResolve("Persian Gulf", out var persian));
        Assert.True(Gazetteer.TryResolve("Arabian Gulf", out var arabian));
        Assert.True(Gazetteer.TryResolve("الخليج العربي", out var arabicScript));

        Assert.Equal(persian, arabian);
        Assert.Equal(persian, arabicScript);
    }

    [Fact]
    public void MalformedUnicodeIsSearchedRatherThanThrown()
    {
        // Feed text is not guaranteed to be well-formed. An unpaired surrogate must not cost the
        // whole observation.
        var place = Gazetteer.FindFirstMention("A report from Odesa \ud800 with a lone surrogate.");

        Assert.Equal("Odesa", place);
    }
}
