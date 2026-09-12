using Geopolitics.Domain;

namespace Geopolitics.UnitTests;

public sealed class GeopoliticalIncidentTests
{
    [Fact]
    public void ConstructorRejectsBlankTitle()
    {
        var act = () => new GeopoliticalIncident(
            Guid.NewGuid(),
            " ",
            "A summary.",
            EventType.Conflict,
            Severity.High,
            DateTimeOffset.UtcNow,
            null,
            ObservationProvenance.Polled,
            DateTimeOffset.UtcNow);

        Assert.Throws<DomainException>(act);
    }

    [Fact]
    public void LinkObservationIsIdempotentAndUpdatesCount()
    {
        var createdAt = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var incident = new GeopoliticalIncident(
            Guid.NewGuid(),
            "Test incident",
            "A valid test summary.",
            EventType.Conflict,
            Severity.Medium,
            createdAt,
            null,
            ObservationProvenance.Polled,
            createdAt);
        var observationId = Guid.NewGuid();

        var firstLinked = incident.LinkObservation(observationId, createdAt.AddMinutes(1));
        var secondLinked = incident.LinkObservation(observationId, createdAt.AddMinutes(2));

        Assert.True(firstLinked);
        Assert.False(secondLinked);
        Assert.Equal(1, incident.ObservationCount);
        Assert.Equal(createdAt.AddMinutes(1), incident.UpdatedAt);
    }
}
