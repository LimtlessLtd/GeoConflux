using Geopolitics.Domain;

namespace Geopolitics.Application.Abstractions;

/// <summary>How a set of coordinates was obtained, so the UI can communicate confidence honestly.</summary>
public enum LocationResolutionMethod
{
    /// <summary>No trustworthy coordinates were available.</summary>
    Unresolved = 0,

    /// <summary>Coordinates came directly from the provider's own structured record.</summary>
    SourceProvided,

    /// <summary>A place name was matched against a deterministic local gazetteer.</summary>
    Gazetteer,
}

/// <param name="Location">Resolved coordinates, or <see langword="null"/> when resolution failed.</param>
/// <param name="Method">Where the coordinates came from.</param>
/// <param name="Confidence">0-1 score describing how certain the resolution is.</param>
/// <param name="FailureReason">Why resolution failed, when it did.</param>
/// <param name="PrecisionNote">
/// How approximate a successful placement is, when it is. Null for a placement precise enough that
/// saying so would be noise; set whenever the coordinate is a centroid standing in for something much
/// larger, so the caller can pass the caveat on rather than discard it.
/// </param>
public sealed record LocationResolution(
    GeoLocation? Location,
    LocationResolutionMethod Method,
    double Confidence,
    string? FailureReason,
    string? PrecisionNote = null)
{
    public bool IsResolved => Location is not null;

    public static LocationResolution Failed(string reason) =>
        new(null, LocationResolutionMethod.Unresolved, 0, reason);
}

/// <summary>
/// Supplies authoritative coordinates. Per ADR 005 a language model may name a place, but only a
/// deterministic resolver may turn that name into latitude and longitude.
/// </summary>
public interface ILocationResolver
{
    Task<LocationResolution> ResolveAsync(LocationResolutionRequest request, CancellationToken cancellationToken);
}

/// <param name="LocationName">Place name claimed by the source or extracted from the payload.</param>
/// <param name="DeclaredLatitude">Latitude supplied by a structured provider, when present.</param>
/// <param name="DeclaredLongitude">Longitude supplied by a structured provider, when present.</param>
/// <param name="DeclaredCountryCode">ISO country code supplied by the provider, when present.</param>
public sealed record LocationResolutionRequest(
    string? LocationName,
    double? DeclaredLatitude,
    double? DeclaredLongitude,
    string? DeclaredCountryCode);
