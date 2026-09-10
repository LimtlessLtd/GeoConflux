namespace Geopolitics.Application.Contracts;

public sealed record IncidentSearch(int Take = 100, DateTimeOffset? From = null, DateTimeOffset? To = null)
{
    public int BoundedTake => Math.Clamp(Take, 1, 250);
}
