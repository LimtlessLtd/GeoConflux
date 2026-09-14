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
somebody wrote. The material to do it with is in the run log and in `coverage.sources` inside every
bundle: how many posts each channel served, how many matched, and which ones could not be read.

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
