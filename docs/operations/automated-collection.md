# Collection that runs without being asked

*Written 2026-09-14. The decision behind it is [ADR 038](../adr/038-collection-without-being-asked.md);
this is how to run, watch and change it.*

Collected OSINT bundles expire fourteen days after they were gathered. Until this was automated, a
repository nobody touched stopped showing collected material a fortnight later — not degraded, gone,
with a warning in a log nobody reads. `.github/workflows/collect.yml` runs a round daily at 06:41
UTC so that stops happening.

## What one round does

```powershell
bash tools/collect.sh                  # every brief in data/osint/briefs
bash tools/collect.sh armed-conflict   # one of them, by id
```

This is the same script the workflow runs, unchanged. In order:

1. **Self-tests the access policy before touching the network.** A refusal misread as permission
   looks exactly like a successful collection, so a broken policy has to stop the round rather than
   be discovered in what it collected.
2. **Collects each brief into a staging directory.** Not into `data/osint` — the pipeline reads that
   directory by glob, and a half-written or empty file sitting there is a bundle as far as it is
   concerned.
3. **Asks `tools/collect/schedule.py` whether the result is worth keeping**, and moves it into
   `data/osint` only if it is.
4. **Prunes bundles past twenty-eight days** from the working tree. They stay in git history.

Running it locally collects and prunes. It cannot push: what reaches the repository is decided
entirely in the workflow.

## Reading the result

Three outcomes, and the middle one is the whole point of the design.

| Outcome | What it means | What happens |
| --- | --- | --- |
| `kept` | The brief collected items not already in today's bundle. | Committed, and the Pages workflow is asked to publish. |
| `quiet` | Channels were read; nothing matched, or nothing was new. | Nothing committed. The run succeeds. |
| `ALARM` | No channel could be read at all. | Nothing committed. **The run fails.** |

A quiet week and a broken collector both produce an empty bundle and a process that exits zero. The
second one, left alone, reports success every morning while the dashboard expires. That is the
failure this is built against, and it is the only condition that turns the run red — because a
scheduled job that goes red when the world is calm gets muted, and then the real failures are muted
too.

An outcome the tooling does not recognise counts as *not read*, so a new failure mode in `collect.py`
raises a false alarm rather than hiding a real one.

`quiet` also covers a second round on a day that already has one. Re-running a brief finds the same
posts with new timestamps on them, and a commit is meant to record a finding rather than a run —
bundles are compared by the content hashes of their items, so an identical set commits nothing.

## Watching it

```powershell
gh run list --workflow=collect.yml --limit 10
gh run view --log                                   # pick a run
git log --oneline -- data/osint                     # rounds that found something
```

`git log data/osint` is a record of rounds that collected, not of rounds that ran — a quiet round
commits nothing deliberately. To see whether the job is running at all, use `gh run list`.

## Changing it

**The cadence** is the `cron:` line in `.github/workflows/collect.yml`. Daily sits inside both
windows that matter: the briefs ask for the previous seven days, and bundles expire at fourteen, so
several consecutive failed runs can pass before anything published goes stale. Collecting more often
than daily mostly re-collects the same items and spends other people's bandwidth doing it.

**What is collected** is `data/osint/briefs/<id>.run.json`, with the prose brief beside it. Changing
that is an editorial act and the schedule will not do it — see ADR 038. Adding a brief needs no
workflow change: the script reads the directory.

**The pruning horizon** is `KEEP_DAYS` (default 28, twice the runtime's `MaxBundleAge`). Keep it
above `MaxBundleAge` or pruning starts deleting bundles the pipeline is still reading.

**A round on demand**: `gh workflow run collect.yml`, or with `-f brief=armed-conflict` for one.

## What it does not do

The round reports per-channel outcomes and refuses to commit nothing. Neither of those notices that a
channel has quietly become useless while still answering 200, that a term has started over-collecting,
or that the register holds a conflict no brief covers. A brief that has drifted out of usefulness
looks, to this workflow, exactly like one that is working.

That is judgement, it changes what the project claims to have looked for, and it belongs in a diff
somebody wrote. What the schedule can do is lay the facts out, which is the section below.

## Reviewing what the rounds actually found

```powershell
python tools/collect/schedule.py --health data/osint
```

Per channel, across every committed bundle: how many rounds could read it, how many rounds it
contributed an item to, how many posts it served in total, and the last date it gave anything.

Counting *rounds* is the point. A channel that answers 200 every morning and has not contributed in a
fortnight is invisible in any single bundle, in the workflow's exit status, and on the published page
— it looks exactly like a channel covering a quiet beat. Only the count separates them.

The report names two shapes and deliberately stops there:

- **Answered every round and contributed nothing.** Either the brief's terms miss what the channel
  publishes, or the channel does not cover this subject.
- **Never readable.** Listed in the brief and never actually collected from.

Neither is automatically a fault, and the tool does not say it is. Both run files already record the
opposite decision for at least one channel — *a source that is listed and produces nothing is a
stated gap; a source quietly dropped from the list is an unstated one* — so the report gives a number
and leaves the judgement where it belongs.

A review worth doing every week or two, with the health table in front of you:

1. `gh run list --workflow=collect.yml` — are the rounds running, and how many alarmed?
2. `--health` — has a channel stopped contributing, and has anything become unreadable that was not?
3. Read a few excerpts from the newest bundle. Terms match as substrings, and the failure that does
   not show up in any count is a term quietly matching the wrong subject.
4. Does the conflict register hold something no brief covers? Two briefs of roughly ten channels is
   the reach this project has, and running them more often does not widen it.

Anything that comes out of that is an edit to `data/osint/briefs/`, in a diff, with the brief's
`revision` raised so bundles collected before and after stay comparable.

## If the run fails

- **`ALARM` on every brief** — the collector is reaching nothing. Check whether the user agent is
  being blocked, whether the channels moved, and whether the runner has a route out. Run
  `python tools/collect/access.py <url>` against one channel to see what it was told.
- **`ALARM` on one brief** — that brief's channels are the problem, not the collector. The other
  briefs still collected and committed.
- **The lint step failed** — a collected bundle did not parse. Nothing was committed and `main` is
  untouched, which is what that gate is for. The bundle is in the run's log output.
- **The push failed twice** — something else is pushing to `main` steadily. The round is discarded;
  the next one collects again.
- **The publish step failed** — bundles were committed but the Pages workflow was not started. Run
  `gh workflow run pages.yml --ref main` to publish them.
