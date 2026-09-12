namespace Geopolitics.Domain;

public enum ObservationKind
{
    Unknown = 0,
    News,
    Satellite,
    ExternalEvent,
    Manual,
    Replay,
}

public static class ObservationKindRules
{
    /// <summary>
    /// Whether a source of this kind may state its own coordinates.
    /// <para>
    /// Only two kinds may, and both are measurements rather than accounts: a satellite instrument
    /// geolocating a pixel, and a curated event dataset publishing a coded location. Those are the
    /// <c>SourceProvided</c> path of ADR 005, and it exists because such a record describes the exact
    /// observation where a gazetteer entry is only the centroid of a named area.
    /// </para>
    /// <para>
    /// Everything else earns a position by naming a place the gazetteer recognises, or stays
    /// unplaced. News does, manual submissions do, and collected bundles do — which matters most,
    /// because an agent reading prose is exactly the actor ADR 005 was written about, and a coordinate
    /// accepted from one would be the failure that ADR prevents arriving through a new door.
    /// </para>
    /// </summary>
    public static bool MayDeclareCoordinates(this ObservationKind kind) =>
        kind is ObservationKind.Satellite or ObservationKind.ExternalEvent;
}
