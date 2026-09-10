# GeoConflux implementation notes

`GeoConflux_Plan.md` is the authoritative specification. An ADR under `docs/adr/` overrides it only when the ADR explicitly records a newer decision.

## Guardrails

- Target .NET 10 and preserve `Domain -> Application -> Infrastructure -> API/Workers` dependency direction.
- The domain project has no framework or infrastructure references.
- Demo/replay data is explicitly labelled and must not be presented as live reporting.
- Treat AI outputs and all external payloads as untrusted input. Never let an LLM authoritatively set coordinates.
- Build, test, and format after meaningful changes. Do not bypass failures.
- Do not add secrets, credentials, or inferred external API results to the repository.

## Local commands

```powershell
dotnet build GeopoliticsDashboard.sln
dotnet test GeopoliticsDashboard.sln
dotnet format GeopoliticsDashboard.sln --verify-no-changes
dotnet run --project src/Geopolitics.Api
```
