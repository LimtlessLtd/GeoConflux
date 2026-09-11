namespace Geopolitics.Application.Abstractions;

/// <param name="Name">Canonical name, matching the gazetteer entry where one exists.</param>
/// <param name="Description">One line on why this passage matters, shown beside the numbers.</param>
/// <param name="WatchRadiusKilometres">
/// How far from the centre point counts as "at" this chokepoint. It varies by feature because the
/// features do: a canal is a few kilometres wide and a strait approach is a hundred, so a single
/// radius would either miss the approaches to one or sweep in unrelated activity around the other.
/// </param>
public sealed record MaritimeChokepoint(
    string Name,
    string Description,
    double Latitude,
    double Longitude,
    double WatchRadiusKilometres);

/// <summary>
/// The maritime passages this system watches.
/// <para>
/// Reference data behind an abstraction so the Application layer can ask "which chokepoints" without
/// owning a table of coordinates, and so a deployment can supply its own list. The positions are
/// representative points on each passage, not survey data.
/// </para>
/// </summary>
public interface IChokepointCatalogue
{
    IReadOnlyList<MaritimeChokepoint> Chokepoints { get; }
}
