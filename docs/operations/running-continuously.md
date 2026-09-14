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

## Retention

Nothing is deleted unless you say so:

```jsonc
"Retention": {
  "Enabled": false,            // shipped off
  "Interval": "24:00:00",
  "Keep": "90.00:00:00"        // measured from when this host received the row
}
```

Off is not caution for its own sake. The rule for this work was *measure before deciding*, and the
holdings panel is the measurement — it reports how many rows a policy would remove today whether or
not one is running, because that count is what the decision is made against. Turning retention on
before reading it would be choosing a policy from a guess about volume.

**What can be deleted, and nothing else:**

| | |
| --- | --- |
| Duplicate observations | A repeat delivery, kept so a redelivery stays auditable. `MarkDuplicate` clears its incident link as part of recording it, so it is behind nothing by construction. |
| Failed observations | Kept so somebody can work out why. Their incident was rolled back with the save that failed. Past the horizon nobody is going to diagnose them. |
| Orphaned inference rows | The audit trail of a model call whose observation no longer exists. [ADR 023](../adr/023-failure-boundary-and-evidence-retention.md) gives these no foreign key so they outlive a *failed save* — which is not the same as outliving the horizon. |

**What is never deleted:** anything an incident rests on. That is ADR 023's line approached from the
other side, and crossing it would leave an incident asserting something with no evidence behind it —
the one artefact this repository refuses to publish. `RetentionBoundaryTests` asserts it against a
real database: it ages every stored row past the horizon, prunes, and then checks that every
incident's evidence still resolves.

**Held claims are also left alone**, and that is a deliberate narrowing of "unlinked". A claim held
for corroboration has no incident, so the plain reading of the rule would allow deleting it. It is
nonetheless drawn on the map and counted in the coverage panel's split of published reporting against
user-generated claims, so removing it would change what the page says about its own composition —
silently. Duplicates and failures are neither shown nor counted there.

**The vacuum matters more than it sounds.** Deleting rows in SQLite moves their pages onto a free
list for reuse and returns nothing to the disk. Without a vacuum the holdings panel would show a
database that never shrinks however much was pruned, and the obvious conclusion — that retention is
not working — would be wrong and unfalsifiable. It runs only when freed pages exceed a tenth of the
database, because a vacuum rewrites the whole file and holds a write lock for the length of it.

The first pass happens one interval after start-up rather than at start-up, so a host restarted while
somebody is reading a failure does not delete that failure as its first act.

**What the panel reports is what this host has done since it started**, not a lifetime total. That is
deliberate: a row per run would outlive a restart and would itself be a second thing to prune, and
the question the figure answers — is the policy actually running — is about the host running now.

## The downtime ledger

A machine that is switched off misses the world, and the world does not tell it so on the way back.
So the host works out, at startup, whether it has been away — and writes the gap down.

**Its only evidence is that an adapter polled.** Observations carry the times *sources* reported, not
the times this process was alive, and a backfill routinely stores records dated years ago. A poll
happening is the one thing that can only be true of a running host, so `IngestionCheckpoints` now
carries a `LastPolledAt` and a `PollEvery` beside the backfill frontier it already held. Every polling
adapter writes them, not only the two that backfill, and it writes them whether the poll succeeded or
not: a provider being unreachable says nothing about whether this machine was switched on.

At startup, the newest poll across all sources is the last moment the host is known to have been
alive. If the gap from there to now exceeds **two of the fastest polling intervals** — and at least
fifteen minutes — it is recorded as a downtime period, from the last poll to now.

- **Two intervals, not one.** One interval's silence is a poll that ran late or a provider that took a
  minute. Two means a source demonstrably missed one.
- **From the last poll, not one interval after it.** Over-covering costs a re-read that deduplication
  absorbs. Under-covering loses records nothing will ever ask for again.
- **The periods are rows, not log lines,** because Sprint 18 has to act on them: a dataset is an
  archive and still holds what happened while the machine was off, so an interval is a window to go
  and ask for; a social feed is a rolling window and does not, so the same interval is a statement
  about what cannot be recovered.

### What it cannot tell you

**If nothing polls, there is no ledger.** Every provider in this repository ships dormant, so a clone
with no credentials has no record of its own uptime at all. The panel says exactly that — *"no source
has ever polled on this host, so it cannot say when it was last running; that is not a statement that
it has always been up"* — rather than reporting no downtime. Enabling any provider starts the ledger.

**The resolution is the fastest poll you run.** With only a daily dataset enabled, a day off the air
is indistinguishable from having been up the whole time. The panel publishes the resolution so the
limit is visible rather than assumed.

The detection runs in `StartAsync` rather than in a background loop, and it is registered before the
ingestion pump. A background service returns to the host at its first await, so the pump would start
and poll while the ledger was still reading — and the first poll overwrites the very timestamp the
gap is measured from.

## Surviving a reboot

`dotnet run` in a terminal dies with the terminal, and a machine that reboots overnight comes back
collecting nothing. The plan offered two ways out — a Windows service or a scheduled task at logon —
and the decision is **a Windows service**.

**A scheduled task at logon fails the requirement it was proposed for.** It does not run until
somebody signs in, so a server that rebooted at 03:00 and is not logged into is a server that is not
collecting, indefinitely, with nothing to show it. (A task triggered *at startup* rather than at logon
does survive a reboot, and it is a reasonable fallback — but it gains nothing over a service and
loses the service control interface.)

**What the service buys, beyond starting at boot:**

- Windows can restart it after a failure. Configured here as three attempts a minute apart, with the
  counter resetting daily. A host that crashes repeatedly should keep trying: the alternative is a
  machine that looks fine and is collecting nothing, which is the failure this whole area exists to
  make visible.
- Shutdown is a lifetime event rather than a kill, so the ingestion queue drains and the processor
  finishes what it had.
- `UseWindowsService()` sets the content root to the binary's directory. Without it a service starts
  in `C:\Windows\System32` and a relative database path resolves there — see *Where the database
  lives* above. It also reports the service as started, which is what stops Windows failing the start
  with error 1053 after thirty seconds.

The call is a no-op on any other platform and when the host is started from a terminal, so one build
serves a container, a developer and a service.

### Installing it

```powershell
# from an elevated PowerShell, in a clone of the repository
.\tools\service\install.ps1 -Path 'C:\GeoConflux'
```

It publishes, registers, configures restart-on-failure, and starts. Re-running it against an existing
service republishes and restarts — that is the upgrade path, and it never touches the database: the
service is stopped, the files are replaced, and it is started again against whatever was there.

`-Path` must be outside the repository. The database lives beside the binaries, and a publish
directory under source control does not survive a `git clean`. The script refuses rather than warns.

`.\tools\service\install.ps1 -Uninstall` stops and removes the service, leaving the published files
and the database alone.

### Credentials for a service

The service account has its own profile, so `dotnet user-secrets` — which writes into the profile of
whoever typed it — is not readable from there. Configuration for a service goes in its environment
block:

```powershell
$key = 'HKLM:\SYSTEM\CurrentControlSet\Services\GeoConflux'
$existing = (Get-ItemProperty $key -Name Environment).Environment
Set-ItemProperty $key -Name Environment -Type MultiString -Value ($existing + @(
  'Providers__Acled__Username=<email>',
  'Providers__Acled__Password=<password>',
  'Providers__Ucdp__AccessToken=<token>'
))
Restart-Service GeoConflux
```

The install script sets `ASPNETCORE_ENVIRONMENT` and `ASPNETCORE_URLS` and preserves everything else
in that block, so re-running it does not remove credentials added this way. It never writes one.

### Confirming it came back

After a reboot:

```powershell
Get-Service GeoConflux
Invoke-RestMethod http://localhost:5266/health/live
Invoke-RestMethod http://localhost:5266/api/operations | ConvertTo-Json -Depth 5
```

The third is the interesting one. If the machine was off for a fortnight, the downtime ledger will
have recorded the gap at startup and the operations report will name it.
