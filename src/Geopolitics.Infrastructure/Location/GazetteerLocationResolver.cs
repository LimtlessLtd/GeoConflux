using Geopolitics.Application.Abstractions;
using Geopolitics.Domain;
using Microsoft.Extensions.Logging;

namespace Geopolitics.Infrastructure.Location;

/// <summary>
/// Supplies coordinates from provider-declared values or the local <see cref="Gazetteer"/>.
/// <para>
/// Per ADR 005 this is the only component permitted to produce latitude and longitude. A language
/// model may propose a place <em>name</em>; if that name is not in the lexicon, the observation is
/// reported as unresolved rather than given plausible-looking coordinates. Keeping the lexicon local
/// also means the pipeline needs no geocoding credentials to run.
/// </para>
/// </summary>
public sealed partial class GazetteerLocationResolver(ILogger<GazetteerLocationResolver> logger) : ILocationResolver
{
    public Task<LocationResolution> ResolveAsync(LocationResolutionRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();

        // A structured provider's own coordinates outrank any name lookup: they describe the
        // observation itself, whereas a gazetteer entry is only the centroid of a named area.
        if (request.DeclaredLatitude is { } latitude && request.DeclaredLongitude is { } longitude)
        {
            var name = string.IsNullOrWhiteSpace(request.LocationName)
                ? FormatCoordinates(latitude, longitude)
                : request.LocationName.Trim();

            // The provider's own statement about its own coordinate, or exact when it made none.
            // Curated event datasets are frank about this — ACLED and UCDP both publish a precision
            // code, and it is frequently not "exact" — so taking every borrowed coordinate as a fix
            // would present a provincial centroid with the confidence of a grid reference.
            var precision = request.DeclaredPrecision ?? LocationPrecision.Exact;

            return Task.FromResult(new LocationResolution(
                new GeoLocation(name, request.DeclaredCountryCode, latitude, longitude, precision),
                LocationResolutionMethod.SourceProvided,
                DeclaredConfidenceFor(precision),
                null,
                DeclaredNoteFor(precision)));
        }

        if (string.IsNullOrWhiteSpace(request.LocationName))
        {
            return Task.FromResult(LocationResolution.Failed("The observation named no location."));
        }

        if (Gazetteer.TryResolve(request.LocationName, out var entry))
        {
            return Task.FromResult(new LocationResolution(
                new GeoLocation(
                    entry.CanonicalName,
                    entry.CountryCode ?? request.DeclaredCountryCode,
                    entry.Latitude,
                    entry.Longitude,
                    ToDomain(entry.Precision)),
                LocationResolutionMethod.Gazetteer,
                ConfidenceFor(entry.Precision),
                null,
                NoteFor(entry)));
        }

        LogUnknownPlace(logger, request.LocationName);
        return Task.FromResult(LocationResolution.Failed($"'{request.LocationName.Trim()}' is not in the local gazetteer."));
    }

    /// <summary>
    /// Confidence for a coordinate the provider supplied, by the precision the provider claimed for
    /// it.
    /// <para>
    /// A flat number here would say that a UCDP record coded to a country centroid is as well placed
    /// as one coded to the event itself, when the record states the opposite. Every value stays above
    /// its gazetteer counterpart below, because a provider that coded a province still knows more
    /// than a name lookup that guessed at one.
    /// </para>
    /// </summary>
    private static double DeclaredConfidenceFor(LocationPrecision precision) => precision switch
    {
        LocationPrecision.Exact => 0.95,
        LocationPrecision.Settlement => 0.85,
        LocationPrecision.Region => 0.6,
        LocationPrecision.Country => 0.35,
        _ => 0.5,
    };

    /// <summary>
    /// The caveat that travels with a coarse provider placement. Silent for exact and settlement
    /// placements, on the same reasoning as the gazetteer notes below: a caveat on every observation
    /// is a caveat nobody reads.
    /// </summary>
    private static string? DeclaredNoteFor(LocationPrecision precision) => precision switch
    {
        LocationPrecision.Country =>
            "The source knew only which country this happened in. The marker shows the country, not "
                + "the location of the event.",
        LocationPrecision.Region =>
            "The source placed this at a representative point for an area rather than at a position. "
                + "The marker shows roughly where, not exactly where.",
        _ => null,
    };

    /// <summary>
    /// Confidence by how much ground the entry stands for. A gazetteer hit is never as good as a
    /// provider fix, and a country hit is a great deal worse than a city one — reporting all three at
    /// the same number would make the figure meaningless.
    /// </summary>
    private static double ConfidenceFor(PlacePrecision precision) => precision switch
    {
        PlacePrecision.Settlement => 0.7,
        PlacePrecision.Region => 0.55,

        // Low on purpose. It says the report is somewhere in this country, which is worth plotting
        // and is not close to knowing where the event was.
        PlacePrecision.Country => 0.3,
        _ => 0.5,
    };

    /// <summary>
    /// The caveat that travels with a coarse placement. Silent for a settlement, because a note on
    /// every observation is a note nobody reads.
    /// </summary>
    private static string? NoteFor(GazetteerEntry entry) => entry.Precision switch
    {
        PlacePrecision.Country =>
            $"Placed at the centroid of {entry.CanonicalName} because no more specific place was named. "
                + "The marker shows the country, not the location of the event.",
        PlacePrecision.Region =>
            $"Placed at a representative point in {entry.CanonicalName}, which is an area rather than a position.",
        _ => null,
    };

    private static LocationPrecision ToDomain(PlacePrecision precision) => precision switch
    {
        PlacePrecision.Region => LocationPrecision.Region,
        PlacePrecision.Country => LocationPrecision.Country,
        _ => LocationPrecision.Settlement,
    };

    private static string FormatCoordinates(double latitude, double longitude) =>
        $"{latitude:F3}, {longitude:F3}";

    [LoggerMessage(Level = LogLevel.Debug, Message = "No gazetteer entry for '{LocationName}'; the observation will be stored without coordinates.")]
    private static partial void LogUnknownPlace(ILogger logger, string locationName);
}
