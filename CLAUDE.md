# GeoConflux implementation notes

`GeoConflux_Plan.md` is the authoritative specification. An ADR under `docs/adr/` overrides it only when the ADR explicitly records a newer decision.

## Guardrails

- Target .NET 10 and preserve `Domain -> Application -> Infrastructure -> API/Workers` dependency direction.
- The domain project has no framework or infrastructure references.
- Demo/replay data is explicitly labelled and must not be presented as live reporting.
- Treat AI outputs and all external payloads as untrusted input. Never let an LLM authoritatively set coordinates.
- Build, test, and format after meaningful changes. Do not bypass failures.
- Do not add secrets, credentials, or inferred external API results to the repository.

## Publishing each increment

**When an increment is finished and verified, merge it to `main` and push.** Completed work must not
be left on a feature branch.

Progress on this project is reviewed through the published dashboard at
<https://limtlessltd.github.io/GeoConflux/>, not by reading diffs, so work that has not reached
`main` is invisible.

There is no separate publishing step. `.github/workflows/pages.yml` deploys on every push to `main`:
it builds, runs the full test suite, executes the real pipeline, exports the snapshot, verifies it is
non-empty, and publishes. The whole rule is therefore *get the verified increment onto `main`*.

```powershell
dotnet build GeopoliticsDashboard.sln
dotnet test GeopoliticsDashboard.sln
dotnet format GeopoliticsDashboard.sln --verify-no-changes
git checkout main
git merge --ff-only <branch>
git push origin main
gh run watch          # confirm the deploy actually succeeded
```

Confirm the workflow succeeded before reporting the increment as published. A failed run means the
page did not update, and the previous page stays live — which is the intended safety behaviour, but
it means "pushed" and "published" are not the same claim.

## Local commands

```powershell
dotnet build GeopoliticsDashboard.sln
dotnet test GeopoliticsDashboard.sln
dotnet format GeopoliticsDashboard.sln --verify-no-changes
dotnet run --project src/Geopolitics.Api
```
