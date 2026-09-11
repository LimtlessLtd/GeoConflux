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

        // A structured provider's own coordinates outrank any name lookup: they describe the exact
        // observation, whereas a gazetteer entry is only the centroid of a named area.
        if (request.DeclaredLatitude is { } latitude && request.DeclaredLongitude is { } longitude)
        {
            var name = string.IsNullOrWhiteSpace(request.LocationName)
                ? FormatCoordinates(latitude, longitude)
                : request.LocationName.Trim();

            return Task.FromResult(new LocationResolution(
                new GeoLocation(name, request.DeclaredCountryCode, latitude, longitude),
                LocationResolutionMethod.SourceProvided,
                0.95,
                null));
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
                    entry.Longitude),
                LocationResolutionMethod.Gazetteer,

                // A centroid for a named region is genuinely less precise than a provider fix,
                // and the confidence reported to the UI says so.
                0.7,
                null));
        }

        LogUnknownPlace(logger, request.LocationName);
        return Task.FromResult(LocationResolution.Failed($"'{request.LocationName.Trim()}' is not in the local gazetteer."));
    }

    private static string FormatCoordinates(double latitude, double longitude) =>
        $"{latitude:F3}, {longitude:F3}";

    [LoggerMessage(Level = LogLevel.Debug, Message = "No gazetteer entry for '{LocationName}'; the observation will be stored without coordinates.")]
    private static partial void LogUnknownPlace(ILogger logger, string locationName);
}
