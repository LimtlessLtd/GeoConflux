namespace Geopolitics.Application.Operations;

/// <summary>
/// What the two-stage spatial search has actually been asked to do on this host.
/// <para>
/// It exists to answer one question with a number rather than a date: <b>when does this project have
/// to move to PostGIS?</b> Sprint 14 owns that migration. This owns writing down the measurement that
/// would set it off, because "when we get big" is not a trigger — it is a way of never deciding.
/// </para>
/// <para>
/// The measurement is not latency. [ADR 017] narrows with an indexable rectangle and then measures
/// exact great-circle distance over what the rectangle returned, and the rectangle's read is capped
/// so that a deliberately wide search cannot pull the table into memory. The cap is the thing to
/// watch: while it is not reached, the two stages together give a complete and exact answer, and the
/// cost is a few hundred rows of trigonometry. The moment it <em>is</em> reached, the answer stops
/// being an answer — the rows beyond the cap were never measured, so "incidents within 50 km"
/// quietly becomes "the most recent capful in the rectangle, then filtered", and the count on the
/// chokepoint panel is the cap rather than the truth.
/// </para>
/// <para>
/// That is not fixable by raising the cap, which trades a wrong answer for a slow one, nor by
/// tuning the index, which can narrow a rectangle and cannot narrow a distance. It is the point at
/// which the missing capability is a spatial index, and it is therefore the trigger.
/// </para>
/// <para>
/// In memory and per host, like the retention log and for the same reason: the question is whether
/// the deployment running now is hitting it.
/// </para>
/// </summary>
public sealed class SpatialScaleLog
{
    private readonly Lock gate = new();

    private long queries;
    private long saturated;
    private int largest;
    private int cap;

    /// <summary>Notes one search: how many rows the rectangle returned, and what it was allowed to return.</summary>
    public void Record(int candidates, int candidateCap)
    {
        lock (gate)
        {
            queries++;
            cap = candidateCap;

            if (candidates > largest)
            {
                largest = candidates;
            }

            if (candidates >= candidateCap)
            {
                saturated++;
            }
        }
    }

    public (long Queries, long Saturated, int Largest, int Cap) Read()
    {
        lock (gate)
        {
            return (queries, saturated, largest, cap);
        }
    }
}
