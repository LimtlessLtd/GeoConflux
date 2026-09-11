namespace Geopolitics.Application.Abstractions;

/// <summary>
/// Scores how alike two pieces of observation text are, on a 0-1 scale.
/// <para>
/// This is the third deduplication layer described in the specification, and it is a seam rather
/// than a single algorithm. Layers one and two — a source identifier and a normalised content hash —
/// catch a byte-identical redelivery. Neither catches two outlets describing the same event in
/// different words, which is the case that matters most for correlation, and which needs a measure
/// of similarity rather than of equality.
/// </para>
/// <para>
/// An implementation must be honest about what it measures. The default is lexical: it compares the
/// words two texts share and makes no claim to understand either. An embedding-backed implementation
/// would measure something closer to meaning, and would need a model to do it. Keeping this an
/// interface means the correlator does not have to care which is installed, and means the offline
/// default cannot be mistaken for the other.
/// </para>
/// </summary>
public interface ITextSimilarity
{
    /// <summary>A short label describing what the score measures, recorded alongside decisions that use it.</summary>
    string Method { get; }

    /// <summary>
    /// Returns 0 when the texts share nothing and 1 when they are equivalent by this measure. Empty
    /// or absent text scores 0: nothing is not similar to anything.
    /// </summary>
    double Score(string? left, string? right);
}
