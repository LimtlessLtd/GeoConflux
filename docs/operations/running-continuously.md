# Running the host, not the build

*Written 2026-09-14, for the deployment described in section 43 of the plan under "Where this runs".*

There are two deployments in this project and they are not the same thing.

**The published page is a snapshot.** Every push to `main` runs the real pipeline, exports what it
produced, and publishes static files. It is genuine output and it is a demonstration. The database it
used is a temporary file that is discarded with the job.

**The live system is the API host**, run continuously on a machine its owner controls. It serves the
same dashboard, maps the SignalR hub, and ships with `SourcesEnabled` and `ProcessorEnabled` true.
Starting it is `dotnet run --project src/Geopolitics.Api`, not a deployment project.

Everything in this document is about the second one. A host that is up for months has problems a
build that lives for seconds does not, and the difference is not scale — it is that the database
survives, which is what makes tempo, baselines and backfill mean anything at all.

---

## Credentials

**Nothing that authenticates goes in this repository.** `appsettings.json` is committed, so it holds
the shape of a setting and never a value: `"ApiKey": ""` is there to show the key exists.

For the running host, the store is .NET user secrets, which lives in the operator's profile and not
in the working tree:

```powershell
cd src/Geopolitics.Api
dotnet user-secrets set "Ai:ApiKey" "<key>"
dotnet user-secrets set "Providers:Acled:Username" "<email>"
dotnet user-secrets set "Providers:Acled:Password" "<password>"
dotnet user-secrets set "Providers:Ucdp:AccessToken" "<token>"
dotnet user-secrets list
```

These are read automatically in the Development environment. For a host running as a service under
`Production`, use environment variables instead — user secrets are a development-time store by
design, and a service account has a different profile from the person who typed the command:

```powershell
$env:Providers__Acled__Username = "<email>"    # double underscore is the section separator
```

Both hosts in this solution carry their own `UserSecretsId`, and they are deliberately different so
a secret set for one is not silently read by the other. `HostProjectTests` fails the build if either
loses it, because the omission is otherwise silent: without an identifier `dotnet user-secrets`
refuses, and the only remaining places to put a credential are an environment variable or a committed
file.

Which credentials are worth having, and what each one buys, is
[dataset-credentials.md](dataset-credentials.md).

## What turning a credential on changes here

The build has no history: it starts with an empty database and lives for seconds, which is why a
backfill cannot run there and why the published page states so plainly. A continuously-running host
is the deployment the backfill, the tempo denominator and the per-conflict baselines were written
for. The same configuration means something different in the two places, and
[dataset-credentials.md](dataset-credentials.md) records which settings are inert in the build.
