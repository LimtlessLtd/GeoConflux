using Geopolitics.Domain;

namespace Geopolitics.Application.Pipeline;

/// <summary>
/// Serialises the correlate-then-commit step for observations of the same category.
/// <para>
/// Correlation reads the incidents it might join and then writes, and nothing in between stopped a
/// second worker holding a report of the same event from reading the same candidates. Neither saw
/// the other's uncommitted write, so both opened an incident and one real event became two. It was a
/// documented limitation of the pipeline and this is the fix.
/// </para>
/// <para>
/// The gate is per category rather than global because that is exactly the contention domain: the
/// candidate query filters by <see cref="EventType"/>, so two observations of different categories
/// cannot see each other's incidents and have no reason to wait for one another. A single global
/// lock would serialise all processing and give up the concurrency the queue exists to provide.
/// </para>
/// <para>
/// This is an in-process lock, which is the right scope for a modular monolith and is not a
/// distributed one. Running two processor hosts against one database would reintroduce the race;
/// that would need a database-level guard, and it is not a configuration this project supports.
/// </para>
/// </summary>
public sealed class CorrelationGate : IDisposable
{
    private readonly SemaphoreSlim[] gates;

    public CorrelationGate()
    {
        // One per enum member, indexed directly. The set is small, fixed at compile time, and never
        // grows at runtime, so a dictionary would add locking of its own to protect the lookup.
        var categories = Enum.GetValues<EventType>().Length;
        gates = new SemaphoreSlim[categories];

        for (var index = 0; index < categories; index++)
        {
            gates[index] = new SemaphoreSlim(1, 1);
        }
    }

    /// <summary>
    /// Waits for exclusive access to one category. Dispose the returned handle to release it; the
    /// caller is expected to hold it across the read, the decision, and the commit, because holding
    /// it across only part of that is the same race in a smaller window.
    /// </summary>
    public async Task<IDisposable> AcquireAsync(EventType eventType, CancellationToken cancellationToken)
    {
        var gate = GateFor(eventType);
        await gate.WaitAsync(cancellationToken);
        return new Release(gate);
    }

    public void Dispose()
    {
        foreach (var gate in gates)
        {
            gate.Dispose();
        }
    }

    /// <summary>
    /// Maps a category to its gate, falling back to the first for any value outside the known set.
    /// An unrecognised category should correlate conservatively rather than throw: a cast from an
    /// unmapped integer is a bug worth surviving, not worth failing an observation over.
    /// </summary>
    private SemaphoreSlim GateFor(EventType eventType)
    {
        var index = (int)eventType;
        return index >= 0 && index < gates.Length ? gates[index] : gates[0];
    }

    private sealed class Release(SemaphoreSlim gate) : IDisposable
    {
        private int released;

        public void Dispose()
        {
            // Guarded because releasing a semaphore twice raises its count and silently lets two
            // holders in, which would be the original race wearing a lock's clothes.
            if (Interlocked.Exchange(ref released, 1) == 0)
            {
                gate.Release();
            }
        }
    }
}
