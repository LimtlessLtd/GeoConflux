# ADR 001: Use a modular monolith

- Status: Accepted
- Context: The platform needs clear boundaries and asynchronous processing but has no demonstrated requirement for independently deployed services.
- Decision: Build a single deployable .NET application with Domain, Application, Infrastructure, API, and Workers projects.
- Consequences: In-process calls are simple and inexpensive while dependency direction remains explicit. Modules can be extracted only if scale or operational requirements demonstrate a need.
