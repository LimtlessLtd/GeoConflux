# ADR 036: A host that stays up states what it holds, deletes only waste, and knows when it was off

- Status: Accepted
- Date: 2026-09-14
- Implements Sprint 17 of [the global coverage assessment](../global-coverage-plan.md).
- Extends [ADR 023](023-failure-boundary-and-evidence-retention.md), whose evidence boundary this
  approaches from the other side: that decision keeps evidence when a commit fails, and this one
  refuses to delete the same evidence when disk pressure suggests it.
- Revises [ADR 017](017-spatial-querying.md) by naming the measurement that ends it, recorded there
  beside the decision it would replace.
- Provides what [ADR 031](031-dataset-history.md)'s checkpoint table was already shaped to hold: a
  per-source record of when this host last asked anything.

## Context

Every deployment problem this project has solved so far belonged to a build. The published page is
produced by a process that starts with an empty database, runs for a few seconds, writes static
files, and exits. Nothing it does can outlive it, so nothing it does can accumulate, corrupt, or be
missed.

Section 43 of the plan describes a second deployment that has existed the whole time and has never
been treated as one: the API host, run continuously on a machine its owner controls. It already
serves the dashboard, maps the hub, and ships with `SourcesEnabled` and `ProcessorEnabled` true.
Sprint 16 made it the interesting deployment rather than a footnote — the register holds 319
conflicts and a credential-free build identifies two of them, because tempo, rolling baselines and
per-conflict narrative were written for a host with history and a build has seconds of it.

What that host was missing was everything about being left alone for months. Six things, and none of
them were hard; what they had in common is that each fails silently.

## Decision

### Credentials have somewhere to live

The API project now carries a `UserSecretsId`. The Workers project had one; the host actually being
run did not, so `dotnet user-secrets set` refused against it and the only remaining places for a key
were an environment variable or `appsettings.json`, which is committed.

The omission was invisible, so it is linted rather than remembered: both host project files are
copied into the test assembly and checked, in the same place and for the same reason as the committed
bundle lint. The two identifiers are asserted to differ, so a secret set for one host is not quietly
read by the other.

### Measurement comes before policy

No retention policy existed anywhere, and there was no number to choose one against. `/api/operations`
now reports row counts per mapped table, the database's own size, the write-ahead log, the pages a
vacuum would return, and what a year at the last seven days' rate would add.

Two of its statements are refusals and they are the useful ones.

**Growth declines to project from less than seven days of records.** Multiplying an hour by 8,760
produces a figure with a year's authority and an hour's support, and it is the figure that gets
quoted. It is the narrative evidence floor from ADR 035 applied to arithmetic.

**A database whose records all arrived within ten minutes is named as a build's own.** The published
page runs against exactly that, and reading its row counts as the holdings of a system that has been
watching would be the most misleading thing this panel could do.

The tables come from the EF model rather than a hand-kept list, so one added by a later migration
appears the day it exists instead of being missing from a figure whose whole job is to be total.

### Retention deletes waste, and the boundary is ADR 023's

Only two kinds of observation are ever deleted: **duplicates**, which are repeat deliveries whose
incident link `MarkDuplicate` clears as part of recording them, and **failures**, whose incident was
rolled back with the save that failed. Orphaned inference rows go with them — ADR 023 gives those no
foreign key so the record of a model call outlives a *failed save*, which is not the same as
outliving a ninety-day horizon.

Nothing an incident rests on is touched, ever. That is ADR 023's line approached from the other
direction, and crossing it would produce an incident asserting something with nothing behind it —
the one artefact this repository has repeatedly refused to publish. Two guards enforce it rather than
one: the status filter is the policy, the null incident link is the invariant underneath it, and an
integration test ages every stored row past the horizon, prunes, and checks that every incident's
evidence still resolves.

**Held claims are excluded, which narrows what the sprint specified.** The sprint said unlinked,
failed and duplicate material. A claim held for corroboration is unlinked, so the plain reading
allows deleting it — and it is also drawn on the map and counted in the coverage panel's split of
published reporting against user-generated claims. Removing it would change what the page says about
its own composition, silently. Duplicates and failures are neither shown nor counted there.

**It ships off**, because the rule for this sprint was measure before deciding and a policy that
switched itself on would be one chosen before anybody read a number. The panel reports the prunable
count whether or not retention is running, because retention off with nothing prunable and retention
off while sitting on a hundred thousand prunable rows are different situations that a flag cannot
tell apart.

The vacuum is not an afterthought. Deleting rows in SQLite moves their pages onto a free list and
returns nothing to the disk, so without it the panel would show a database that never shrinks however
much was pruned — and the obvious conclusion, that retention was not working, would be wrong and
unfalsifiable.

### Write-ahead logging, and a backup that is one

The store is put into WAL mode at startup, every startup. The default rollback journal has a writer
exclude every reader for the length of its transaction: invisible in a build that writes for seconds
with nobody reading, and the shape of a stall in a host where two processor workers write while a
dashboard, a hub and a health check read. It is re-applied each start because the mode lives in the
file, so a database restored from a backup arrives in whatever mode it was in. A store that cannot
take it — a network share — logs a warning and carries on; a performance property is not a reason to
refuse to start.

`synchronous` stays at `FULL`. The usual companion change to WAL is `NORMAL`, which can lose the last
transactions to an operating-system crash. Nobody here has measured a need for it, and the records it
would lose are the most recent ones.

**That makes copying the file wrong, and wrong quietly.** In WAL mode the database is three files,
only one of which anybody would think to copy, and a committed row can sit in the log until a
checkpoint moves it — so a file copy opens, reads, and is missing data, with no error and no warning.
The backup uses SQLite's online backup API, writes under a partial name, verifies with
`PRAGMA quick_check`, and only then rotates older copies out: a run that produces an unreadable copy
must not also be the run that deleted the last readable one. The test demonstrates the failure rather
than describing it, taking a hand-written file copy and a proper one from the same instant and
comparing what each holds.

**There is no default destination.** The only possible default is beside the original, which survives
a bad write and a mistaken delete and not a failed disk, and the deployment most likely to keep a
default is the one least likely to notice. When the destination is on the same volume the host says
so, in the log and on the page, rather than showing a healthy-looking count of copies. What the page
reports is the directory rather than the scheduler: a schedule failing for a fortnight reports a
fortnight of attempts, and the directory reports a fortnight-old copy.

### Surviving a reboot is a service, not a logon task

The plan offered a Windows service or a scheduled task at logon. It is a service, because the task
fails the requirement it was proposed for: it does not run until somebody signs in, so a server that
rebooted at three in the morning and is not logged into stays down indefinitely with nothing to show
it.

`UseWindowsService()` earns its package on three counts. Windows can restart the host after a failure
— three attempts a minute apart, counter resetting daily — and a machine that looks fine and is
collecting nothing is exactly the failure this whole sprint exists to make visible. Shutdown becomes
a lifetime event rather than a kill, so the queue drains. And it sets the content root to the binary's
directory, without which a service starting in `C:\Windows\System32` resolves the shipped relative
database path *there*: an empty database, a healthy-looking host, and none of the history it was left
running to accumulate.

The last of those was a real defect rather than a hypothetical one, and it is fixed independently of
the service: a relative `Data Source` is now anchored to the content root — the project directory
under `dotnet run`, the binary's directory under a service — so a developer's existing database does
not move and a service's is beside its executable.

### The downtime ledger is built on the only evidence available

A machine that is switched off misses the world, and the world does not tell it so on the way back.
The only evidence a host has of having been alive at a given moment is that one of its adapters asked
a provider something then: observations carry the times *sources* reported, not the times this
process was up, and a backfill routinely stores records dated years ago.

So `IngestionCheckpoints` gains a last-polled timestamp and the cadence that produced it. Every
polling adapter writes them, not only the two that backfill, and writes them whether the poll
succeeded or not — a provider being unreachable says nothing about whether this machine was switched
on. The backfill frontier becomes nullable as part of this, because a row created by a poll must not
answer a question the poll did not ask: a frontier invented there would tell the walk that history
from that moment had already been requested, and it would stop short of where it should with nothing
to show it.

At startup the gap from the newest poll to now becomes a `DowntimePeriod` if it exceeds two of the
fastest polling interval and at least fifteen minutes. **Two intervals, not one**, because one is a
poll that ran late. **From the last poll rather than one interval after it**, because over-covering
costs a re-read that deduplication absorbs and under-covering loses records nothing will ask for
again. **Rows rather than log lines**, because Sprint 18 has to act on them.

Detection runs in `StartAsync` and is registered before the ingestion pump. A background service
returns to the host at its first await, and the pump's first poll overwrites the very timestamp the
gap is measured from. It is also the one containment in this project that had to be got right in the
other direction: an exception out of `StartAsync` aborts host startup, so an unwritten ledger row
would take down the collection this host exists to do — trading a record of an outage for an outage.
The gap is lost and the host runs.

**The case that ships is the one that had to be got right.** Every provider in this repository is
dormant without a credential, so a clone polls nothing and has no record of its own uptime at all.
The panel says exactly that — *no source has ever polled on this host, so it cannot say when it was
last running; that is not a statement that it has always been up* — rather than reporting no
downtime. Conflating no evidence with no downtime is the same error as an empty map reading as peace.

### The PostGIS trigger is a candidate cap, not a date

Sprint 14 owns the migration. This sprint owns the measurement, and looking for it turned up
something sharper than a latency threshold.

`SpatialQueryService` pulls at most 1,000 rows from its bounding box before measuring exact distances
over them. Below that cap the two stages give a complete and exact answer. At it, the rows beyond the
cap are never measured, so "incidents within 50 km" silently becomes "the most recent thousand in the
rectangle, then filtered", and the chokepoint panel's count becomes the cap rather than a count.

The trigger is therefore a search returning the full cap: a **correctness** failure, and a silent
one. It is not fixable inside ADR 017 — raising the cap trades a wrong answer for a slow one, and
tuning the index does nothing, because an index can narrow a rectangle and cannot narrow a distance.
Every search records how many rows its rectangle returned; the report publishes it and the page
speaks up the moment one reaches the cap.

## Consequences

- **Nothing here changes what a clone does.** Retention and backups both ship off, the ledger records
  nothing without a polling source, and the published page's only visible change is a panel saying
  that its database was created for that build and discarded with it.
- **Four of the six are off by default, and that is the same pattern as FIRMS, ACLED and UCDP.** Two
  of them are destructive or write to disk, one needs a destination that cannot be guessed, and one
  needs a credential elsewhere. What ships is the capability and the measurement, not the policy.
- **Retention changes what this host can say about its own past.** Counts of duplicates and failures
  are historical figures like any other, and once they are gone the only record that they existed is
  that something removed them. The report says so where the deletion count is shown.
- **The downtime ledger's resolution is the fastest poll a deployment runs.** With only a daily
  dataset enabled, a day off the air cannot be told from a day of quiet. The report publishes the
  resolution rather than leaving it assumed.
- **The retention and spatial figures are per host rather than durable**, deliberately. A row per run
  would outlive a restart and would itself be a second thing to prune, and the question each answers
  — is this policy running, is this search still complete — is about the host running now. The report
  says which and since when, so neither can be read as a lifetime total.
- **One package was added**, `Microsoft.Extensions.Hosting.WindowsServices`, recorded as a dated
  addendum to the dependency review rather than by rewriting a review that measured one day.
- **The Windows service path is documented and unverified by any automated check.** CI runs on
  Ubuntu, so nothing in the suite exercises `sc.exe`, the service control manager, or the registry
  environment block the install script writes. This is the same standing caveat the container build
  already carries, and the operations document repeats it where somebody is about to run the script.
  What *is* covered is the code change behind it: the content-root anchoring of a relative database
  path is a unit-testable decision and is tested.
