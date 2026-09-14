using Geopolitics.Domain;

namespace Geopolitics.Application.Abstractions;

/// <summary>
/// The register of conflicts this system knows about, and where it came from.
/// <para>
/// An abstraction because the register is assembled in infrastructure — it is read from a committed
/// extract of a coding project's own dataset, and its geography is worked out by resolving that
/// project's place names through the gazetteer — while everything that reasons about membership is
/// application logic. The dependency runs one way, as it does for the lexicon.
/// </para>
/// <para>
/// Deliberately read-only from here. A register the application could add to would be a register
/// this project writes, and the entire point of it is that this project does not.
/// </para>
/// </summary>
public interface IConflictRegister
{
    /// <summary>Every conflict in the register, largest by coded events first.</summary>
    IReadOnlyList<Conflict> All { get; }

    /// <summary>
    /// Where the register came from, in one line, for display beside any count derived from it. A
    /// figure whose denominator is unattributed invites the reader to assume it is this system's own
    /// list, which is the assumption this whole design exists to remove.
    /// </summary>
    string Provenance { get; }

    /// <summary>
    /// Words dropped from party names because they turned out to name conflict in general rather than
    /// any party in particular. Reported rather than hidden: each one is a word a report could use and
    /// this system will not match on.
    /// </summary>
    IReadOnlyList<string> AmbiguousActorWords { get; }

    bool TryGet(string? key, out Conflict conflict);
}
