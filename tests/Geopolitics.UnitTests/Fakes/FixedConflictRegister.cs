using Geopolitics.Application.Abstractions;
using Geopolitics.Domain;

namespace Geopolitics.UnitTests.Fakes;

/// <summary>
/// A register of exactly the conflicts a test says it holds.
/// <para>
/// Empty by default, so a pipeline test that is about correlation or deduplication is not implicitly
/// also a test of the 319-conflict extract. A test about membership supplies the two or three entries
/// it means to reason about, which is the difference between an assertion and a coincidence.
/// </para>
/// </summary>
public sealed class FixedConflictRegister(params Conflict[] conflicts) : IConflictRegister
{
    public IReadOnlyList<Conflict> All { get; } = conflicts;

    public string Provenance => "a fixture";

    public IReadOnlyList<string> AmbiguousActorWords { get; } = ConflictActorIndex.Prune(conflicts);

    public bool TryGet(string? key, out Conflict conflict)
    {
        conflict = All.FirstOrDefault(entry => entry.Key == key)!;
        return conflict is not null;
    }
}
