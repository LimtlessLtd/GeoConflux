using Geopolitics.Application.Abstractions;
using Geopolitics.Domain;
using Geopolitics.Infrastructure.Location;
using Microsoft.Extensions.Logging.Abstractions;

namespace Geopolitics.UnitTests;

/// <summary>
/// A borrowed coordinate carries the precision its provider claimed for it.
/// <para>
/// <see cref="ObservationKindRules.MayDeclareCoordinates"/> lets a curated event dataset state its
/// own position, and that rule is correct. What it does not settle is how good that position is, and
/// the datasets themselves are frank about it: ACLED codes <c>geo_precision</c>, UCDP codes
/// <c>where_prec</c>, and in both the coordinate is often a town centroid or a provincial one rather
/// than the event. Flattening all of that to "exact" would take a record that says "somewhere in this
/// province" and draw it with the confidence of a grid reference — the same overstatement ADR 005
/// refuses when a model proposes a coordinate, arriving instead through a provider that was honest
/// about its own limits.
/// </para>
/// </summary>
public sealed class DeclaredPrecisionTests
{
    private static readonly GazetteerLocationResolver Resolver =
        new(NullLogger<GazetteerLocationResolver>.Instance);

    [Fact]
    public async Task AProviderThatStatesNoPrecisionIsTakenAsExact()
    {
        // Which is right for the one source here that measures a position rather than assigning one:
        // a satellite instrument geolocating a pixel.
        var resolution = await Resolver.ResolveAsync(
            new LocationResolutionRequest("Detected hotspot", 15.462, 45.325, "YE"),
            CancellationToken.None);

        Assert.Equal(LocationPrecision.Exact, resolution.Location!.Precision);
        Assert.Equal(LocationResolutionMethod.SourceProvided, resolution.Method);
        Assert.Null(resolution.PrecisionNote);
    }

    [Theory]
    [InlineData(LocationPrecision.Exact)]
    [InlineData(LocationPrecision.Settlement)]
    [InlineData(LocationPrecision.Region)]
    [InlineData(LocationPrecision.Country)]
    public async Task ThePrecisionTheProviderStatedIsThePrecisionRecorded(LocationPrecision declared)
    {
        var resolution = await Resolver.ResolveAsync(
            new LocationResolutionRequest("Marib", 15.462, 45.325, "YE", declared),
            CancellationToken.None);

        Assert.Equal(declared, resolution.Location!.Precision);
    }

    /// <summary>
    /// Confidence has to move with precision or the number means nothing. A record coded to a country
    /// centroid is not as well placed as one coded to the event, and the record says so.
    /// </summary>
    [Fact]
    public async Task ConfidenceFallsAsTheStatedPrecisionCoarsens()
    {
        var confidences = new List<double>();

        foreach (var precision in new[]
        {
            LocationPrecision.Exact,
            LocationPrecision.Settlement,
            LocationPrecision.Region,
            LocationPrecision.Country,
        })
        {
            var resolution = await Resolver.ResolveAsync(
                new LocationResolutionRequest("Marib", 15.462, 45.325, "YE", precision),
                CancellationToken.None);

            confidences.Add(resolution.Confidence);
        }

        Assert.Equal(confidences.OrderByDescending(value => value), confidences);
        Assert.True(confidences.Distinct().Count() == confidences.Count, "Each precision should score differently.");
    }

    /// <summary>
    /// A provider that coded a province still knows more than a name lookup that guessed at one, so
    /// the declared scale stays above the gazetteer scale at every level.
    /// </summary>
    [Fact]
    public async Task ADeclaredPlacementOutranksAGazetteerOneAtTheSamePrecision()
    {
        var declared = await Resolver.ResolveAsync(
            new LocationResolutionRequest("Yemen", 15.55, 48.52, "YE", LocationPrecision.Country),
            CancellationToken.None);

        var looked = await Resolver.ResolveAsync(
            new LocationResolutionRequest("Yemen", null, null, null),
            CancellationToken.None);

        Assert.Equal(LocationPrecision.Country, declared.Location!.Precision);
        Assert.Equal(LocationPrecision.Country, looked.Location!.Precision);
        Assert.True(declared.Confidence > looked.Confidence);
    }

    [Fact]
    public async Task ACoarsePlacementCarriesACaveatAndAPreciseOneDoesNot()
    {
        var country = await Resolver.ResolveAsync(
            new LocationResolutionRequest("Ethiopia", 9.15, 40.49, "ET", LocationPrecision.Country),
            CancellationToken.None);

        var region = await Resolver.ResolveAsync(
            new LocationResolutionRequest("Tigray", 13.5, 39.47, "ET", LocationPrecision.Region),
            CancellationToken.None);

        var settlement = await Resolver.ResolveAsync(
            new LocationResolutionRequest("Mekelle", 13.497, 39.475, "ET", LocationPrecision.Settlement),
            CancellationToken.None);

        Assert.Contains("only which country", country.PrecisionNote!, StringComparison.Ordinal);
        Assert.Contains("roughly where, not exactly where", region.PrecisionNote!, StringComparison.Ordinal);

        // Silent for a settlement, on the same reasoning as the gazetteer notes: a caveat on every
        // observation is a caveat nobody reads.
        Assert.Null(settlement.PrecisionNote);
    }
}
