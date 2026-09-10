# ADR 005: Separate semantic extraction from authoritative coordinates

- Status: Accepted
- Context: Language models can identify place names but may hallucinate or misplace latitude/longitude.
- Decision: AI may extract a location name. An `ILocationResolver` using a deterministic gazetteer/geocoder supplies coordinates and resolution metadata.
- Consequences: Unresolved observations remain persisted and visible. Coordinates are never invented to keep the map populated.
