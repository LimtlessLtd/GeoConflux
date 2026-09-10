using Geopolitics.Domain;

namespace Geopolitics.UnitTests;

public sealed class GeoLocationTests
{
    [Fact]
    public void ConstructorRejectsLongitudeOutsideValidRange()
    {
        var act = () => new GeoLocation("Somewhere", "GB", 51.5, 180.01);

        Assert.Throws<DomainException>(act);
    }
}
