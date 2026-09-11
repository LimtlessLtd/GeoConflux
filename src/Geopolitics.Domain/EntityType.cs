namespace Geopolitics.Domain;

/// <summary>
/// Category of a named entity extracted from observation text. Deliberately coarse: a finer
/// taxonomy would demand a precision the extraction step cannot deliver, and the dashboard only
/// needs enough structure to group evidence and to support entity-overlap correlation.
/// </summary>
public enum EntityType
{
    Unknown = 0,
    Person,
    Organisation,
    State,
    MilitaryUnit,
    Vessel,
    Place,
}
