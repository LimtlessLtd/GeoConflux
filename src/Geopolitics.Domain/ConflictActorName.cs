namespace Geopolitics.Domain;

/// <summary>
/// Turns the name of a party to a conflict into the words that identify it.
/// <para>
/// Coding projects write parties formally — "Government of Ukraine", "Houthi movement (Ansar
/// Allah)", "Forces democratiques de liberation du Rwanda". Reporting writes them any way it likes.
/// Comparing the two as whole strings matches almost nothing, so what is compared is words.
/// </para>
/// <para>
/// Deliberately *not* a stopword list. Which words identify a party and which are furniture is not a
/// judgement this file should make: "government", "forces" and "islamic" are everywhere and "houthi"
/// is not, and that is a countable property of the register rather than an opinion about conflict.
/// The register measures it and discards the words it finds everywhere, which is why this class only
/// has to split text into words and say how two words are compared.
/// </para>
/// </summary>
public static class ConflictActorName
{
    /// <summary>
    /// Shortest word kept. Two-letter tokens are the problem this guards against: "IS" is a real
    /// armed group and also the commonest verb in English, and a register that matched on it would
    /// assign half the world's reporting to one conflict. Three lets through acronyms that are
    /// genuinely distinctive — PKK, FDN, JNIM — and the register's own frequency filter removes the
    /// three-letter words that are not.
    /// </summary>
    public const int MinimumTokenLength = 3;

    /// <summary>
    /// Shortest token that may match as a prefix rather than exactly. Reporting inflects party names
    /// constantly — Houthi/Houthis, Ukraine/Ukrainian — and a prefix handles that without a stemmer.
    /// Four is the floor because below it prefixes stop being inflections: "ira" opens Iran, Iraq and
    /// the IRA, which are three different things.
    /// </summary>
    public const int MinimumPrefixLength = 4;

    /// <summary>
    /// The words in a party's name, lowercased, in the order they appear. Anything that is not a
    /// letter or digit separates words, because these names arrive with hyphens, apostrophes,
    /// parentheses and the occasional slash, and none of those are part of the word.
    /// </summary>
    public static IReadOnlyList<string> Tokenise(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return [];
        }

        var tokens = new List<string>();
        var current = new System.Text.StringBuilder();

        foreach (var character in name)
        {
            if (char.IsLetterOrDigit(character))
            {
                current.Append(char.ToLowerInvariant(character));
                continue;
            }

            Flush(tokens, current);
        }

        Flush(tokens, current);
        return tokens;
    }

    /// <summary>
    /// Whether two words name the same thing. Equal, or one is a prefix of the other and the shorter
    /// is long enough for that to mean an inflection rather than a coincidence.
    /// </summary>
    public static bool Same(string left, string right)
    {
        if (string.Equals(left, right, StringComparison.Ordinal))
        {
            return true;
        }

        var (shorter, longer) = left.Length < right.Length ? (left, right) : (right, left);

        return shorter.Length >= MinimumPrefixLength && longer.StartsWith(shorter, StringComparison.Ordinal);
    }

    private static void Flush(List<string> tokens, System.Text.StringBuilder current)
    {
        if (current.Length >= MinimumTokenLength)
        {
            var token = current.ToString();

            if (!tokens.Contains(token, StringComparer.Ordinal))
            {
                tokens.Add(token);
            }
        }

        current.Clear();
    }
}
