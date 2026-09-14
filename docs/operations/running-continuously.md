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

## Where the database lives

The shipped connection string is `Data Source=geopolitics.db`, a relative path. SQLite resolves a
relative path against the process working directory, which is fine for `dotnet run` and wrong for a
service — a Windows service starts in `C:\Windows\System32`, and the host would create an empty
database there, report itself healthy, and hold none of the history it was left running to
accumulate. Nothing fails; the data is simply somewhere else.

So a relative path is now anchored to the **content root** instead: the project directory under
`dotnet run`, the binary's directory under a service. An absolute path, a `file:` URI and an
in-memory database are left exactly as written.

If you want the database somewhere specific — a data volume, a different disk — say so outright:

```powershell
$env:ConnectionStrings__Geopolitics = "Data Source=D:\geoconflux\geopolitics.db"
```

## Write-ahead logging

The database is put into WAL mode at startup, every startup. In SQLite's default rollback journal a
writer excludes every reader for the length of its transaction; that is invisible in a build, which
writes for a few seconds with nobody reading, and it is the shape of a stall in a host where two
processor workers write while a dashboard, a hub and a health check read.

It is re-applied on every start rather than once because the mode lives in the database file, so a
file restored from a backup or copied from another machine arrives in whatever mode it was in. If it
cannot be applied — a database on a network share cannot use it at all — the host logs a warning and
carries on. A performance property is not a reason to refuse to start.

`synchronous` is deliberately left at `FULL`. The usual companion change to WAL is `NORMAL`, which
can lose the last transactions to an operating-system crash. Nobody here has measured a need for it,
and the records it would lose are the most recent ones.

## Backups

**Copying the database file is wrong, and it fails quietly.** In WAL mode the database is three
files, only one of which is the one anybody would think to copy, and a committed row can be sitting
in the log rather than in the database until a checkpoint moves it. A file copy therefore produces
something that opens, reads, and is missing data — with no error and no warning. `SqliteBackupTests`
demonstrates exactly that: it takes a hand-written file copy and a proper one from the same instant
and compares what each one holds.

The host uses SQLite's online backup API, which reads a consistent snapshot while writers carry on.
Each copy is written under a `.partial` name, verified with `PRAGMA quick_check`, and only renamed
into place if it verifies. Older copies are rotated **after** that, never before: a run that produces
an unreadable copy must not also be the run that deleted the last readable one.

**There is no default destination**, and that is the decision rather than an omission:

```jsonc
"Backup": {
  "Directory": "E:\backups\geoconflux",   // empty means no backup runs at all
  "Interval": "24:00:00",                   // measured from the newest copy on disk, not from start-up
  "Keep": 7
}
```

The only possible default would be beside the database, which survives a bad write, a bad migration
and a mistaken delete — and not a failed disk. The host says which of the two you have: if the
destination is on the same volume as the database it logs a warning at every run, and the operations
panel states it in as many words rather than showing a healthy-looking count of copies.

The interval is measured from the newest copy already on disk rather than from process start, so a
host that restarts twice a day still backs up once, and a host that restarts constantly does not back
up constantly.

**What the panel reports is the directory, not the scheduler.** A service that has been failing for a
fortnight reports a fortnight of attempts; the directory reports a fortnight-old copy. The second is
the fact that matters, and a status flag would have hidden it.
