using Geopolitics.Application.Contracts;
using Geopolitics.Domain;

namespace Geopolitics.Application.Abstractions;

/// <summary>
/// Turns a provider-shaped envelope into a domain observation with a title, summary, category,
/// severity, and event time. Normalisation is deterministic and never contacts an external service,
/// so a source outage or an AI outage cannot prevent an observation from being recorded.
/// </summary>
public interface IObservationNormaliser
{
    RawObservation Normalise(ObservationEnvelope envelope, DateTimeOffset receivedAt);
}
