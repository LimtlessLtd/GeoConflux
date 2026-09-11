using System.Collections.Frozen;
using System.Text;
using Geopolitics.Application.Abstractions;

namespace Geopolitics.Application.Pipeline;

/// <summary>
/// Measures how many meaningful words two texts share.
/// <para>
/// This is deliberately not an embedding model and is named so that nobody can mistake it for one.
/// It is the measure that is always available: no provider, no credential, no network, and the same
/// answer on every run, which is what lets correlation behaviour be reproduced in a test and in the
/// published snapshot. Two wire reports of one event share the nouns that matter — a strait, a vessel
/// type, an actor — while two unrelated reports of the same category do not, and that difference is
/// most of what this needs to detect.
/// </para>
/// <para>
/// What it cannot do is recognise a paraphrase with no shared vocabulary, or the same event reported
/// in two languages. Those need a real embedding model, and the correlator guards against trusting
/// this measure alone precisely because of that gap.
/// </para>
/// </summary>
public sealed class LexicalTextSimilarity : ITextSimilarity
{
    /// <summary>
    /// Tokens too common to carry evidence. Without this, any two English sentences share "the",
    /// "and", and "of", and every comparison is dragged towards a misleadingly high score.
    /// </summary>
    private static readonly FrozenSet<string> Stopwords = new[]
    {
        "a", "about", "after", "against", "all", "also", "an", "and", "any", "are", "as", "at",
        "be", "been", "before", "being", "but", "by", "can", "could", "did", "do", "for", "from",
        "further", "had", "has", "have", "he", "her", "his", "how", "i", "if", "in", "into", "is",
        "it", "its", "more", "most", "near", "no", "not", "of", "on", "one", "only", "or", "other",
        "our", "out", "over", "reported", "reportedly", "said", "say", "says", "she", "should",
        "so", "some", "such", "than", "that", "the", "their", "them", "then", "there", "these",
        "they", "this", "those", "to", "two", "under", "up", "was", "we", "were", "what", "when",
        "where", "which", "while", "who", "why", "will", "with", "would", "you", "your",
    }.ToFrozenSet(StringComparer.Ordinal);

    /// <summary>
    /// Tokens shorter than this are dropped. They are overwhelmingly articles, initials, and
    /// fragments left behind by punctuation, and they add noise rather than evidence.
    /// </summary>
    private const int MinimumTokenLength = 3;

    /// <summary>
    /// Ceiling on how much text is compared. Similarity is decided by the opening of a report in
    /// practice, and an unbounded comparison would let one very long payload dominate the cost of
    /// processing a batch.
    /// </summary>
    private const int MaxComparedCharacters = 2_000;

    public string Method => "lexical-overlap";

    public double Score(string? left, string? right)
    {
        if (string.IsNullOrWhiteSpace(left) || string.IsNullOrWhiteSpace(right))
        {
            return 0;
        }

        var leftTokens = Tokenise(left);
        var rightTokens = Tokenise(right);

        if (leftTokens.Count == 0 || rightTokens.Count == 0)
        {
            return 0;
        }

        // Iterate the smaller set against the larger one, which is the same count either way and
        // cheaper when the two texts differ a lot in length.
        var (smaller, larger) = leftTokens.Count <= rightTokens.Count
            ? (leftTokens, rightTokens)
            : (rightTokens, leftTokens);

        var shared = smaller.Count(token => larger.Contains(token));

        // Otsuka-Ochiai, the set-theoretic form of cosine similarity. Jaccard would be the obvious
        // alternative and is the wrong one here: a two-line agency snap and a six-paragraph article
        // about the same event have very different token counts, and Jaccard penalises that
        // difference as though it were disagreement. Dividing by the geometric mean does not.
        return Math.Round(shared / Math.Sqrt((double)smaller.Count * larger.Count), 4);
    }

    /// <summary>
    /// Reduces text to the set of meaningful words it contains. A set rather than a list: a word
    /// repeated ten times in a long article is not ten times the evidence that it is the same event.
    /// </summary>
    private static HashSet<string> Tokenise(string text)
    {
        var span = text.Length <= MaxComparedCharacters ? text.AsSpan() : text.AsSpan(0, MaxComparedCharacters);
        var tokens = new HashSet<string>(StringComparer.Ordinal);
        var builder = new StringBuilder(32);

        foreach (var character in span)
        {
            if (char.IsLetterOrDigit(character))
            {
                builder.Append(char.ToLowerInvariant(character));
                continue;
            }

            // A hyphen joins a name more often than it separates words in this domain —
            // "Bab-el-Mandeb" is one token, and splitting it would discard the strongest signal in
            // the sentence.
            if (character is '-' && builder.Length > 0)
            {
                builder.Append('-');
                continue;
            }

            Flush(builder, tokens);
        }

        Flush(builder, tokens);
        return tokens;
    }

    private static void Flush(StringBuilder builder, HashSet<string> tokens)
    {
        if (builder.Length == 0)
        {
            return;
        }

        var token = builder.ToString().Trim('-');
        builder.Clear();

        if (token.Length >= MinimumTokenLength && !Stopwords.Contains(token))
        {
            tokens.Add(token);
        }
    }
}
