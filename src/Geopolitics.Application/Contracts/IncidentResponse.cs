using Geopolitics.Domain;

namespace Geopolitics.Application.Contracts;

public sealed record IncidentResponse(
    Guid Id,
    string Title,
    string Summary,
    EventType EventType,
    Severity Severity,
    DateTimeOffset OccurredAt,
    LocationResponse? Location,
    int ObservationCount,
    bool IsDemo);

public sealed record LocationResponse(string Name, string? CountryCode, double Latitude, double Longitude);
