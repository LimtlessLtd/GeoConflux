# ADR 002: Start with SQLite and defer SpatiaLite activation

- Status: Accepted
- Context: Local setup and CI must work without external services; later spatial semantics require more than coordinate storage.
- Decision: Use EF Core with SQLite for the foundation. Add SpatiaLite behind graceful capability detection in the spatial sprint.
- Consequences: The app runs locally without credentials. Spatial features will not claim availability until the native extension is verified on the host.
