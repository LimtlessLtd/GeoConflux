# ADR 038: Running a brief is automated; writing one is not

- Status: Accepted
- Date: 2026-09-14
- Extends [ADR 025](025-agent-collected-osint.md), which made collection a separate act from
  ingestion and delivered its result as a committed file. This decision schedules that act without
  changing what it produces.
- Bounded by [ADR 030](030-collection-access-policy.md). Every request a scheduled round makes goes
  through the same access policy, and the policy is checked before the round reaches the network
  rather than after.

## Context

Collected bundles expire fourteen days after they were gathered. `AgentBriefEventSource` skips an
older one with a warning rather than serving it, which is correct — a page describing week-old posts
as current is the failure that decision exists to prevent — and it has a consequence nobody had
acted on. A repository left alone stops showing collected material a fortnight after the last time
somebody remembered to run the collector. Not degraded, not stale: gone, with a warning in a log
nobody is reading.

That was acceptable while collection was a thing done deliberately during a sprint. It stops being
acceptable the moment the dashboard is the thing progress is judged by, because the failure is
invisible from outside. The page does not say "nobody has collected anything since the fourteenth".
It shows the polled feeds, which still work, and simply has less on it.

The obvious fix is a scheduled job, and the reason one had not been written is that ADR 025 went out
of its way to make collection a separate, deliberate act. That looked like it might be the point
rather than an accident of how it was first done, so it was worth checking before automating it
away.

## Decision

### The line is between executing tasking and authoring it

ADR 025 says *tasking is configuration, not conversation*. A brief is a committed file: the channels,
the terms, the caps, and a prose document beside it explaining what is in scope and why. Running that
file against the channels it already names is the same act every day and produces a result a reader
can check. Deciding that a channel has gone dark, that a term is over-collecting, or that the register
holds a conflict no brief covers — that is authorship, it changes what the project claims to have
looked for, and it belongs in a diff somebody wrote.

So the schedule runs briefs. It does not write them, extend them, or adjust a cap because a run came
back thin. `.github/workflows/collect.yml` reads `data/osint/briefs` and has no other source of
instruction.

### Committing unattended keeps the properties that made a file the delivery

ADR 025 chose a committed file over a runtime model call for three properties: no credential, no
outbound connection from the application, and the same result on every run — "the three properties
that let the published dashboard show cited material rather than a recorded demo, and that let a
reviewer check the citations in a diff."

A scheduled round keeps all three. `collect.py` needs no credential, the application still reads only
from disk, and the bundle is still a file whose citations sit in a diff. What changes is that nobody
has read that diff at the moment it lands. That is worth being precise about rather than waving
past: the ADR claimed reviewability, not review. A commit is reviewable afterwards and permanently,
which is more than most of what reaches a repository on a schedule can say.

What it does give up is a person glancing at the result and noticing something wrong with it that is
not wrong with its structure — a channel that started serving syndicated filler, a term that began
matching something it should not. Nothing in this decision recovers that, and the section below says
so plainly rather than implying the lint covers it.

### An empty bundle has two opposite meanings and the automation has to pick one

This is the part a person did implicitly and a schedule cannot.

A round that reads every channel and matches nothing has found a quiet week. A round where no channel
could be read has found a broken collector — a blocked user agent, a moved endpoint, a network with
no route out. Both end with an empty bundle and a process that exited zero. Left alone, the second
one reports success every morning while the dashboard expires.

`tools/collect/schedule.py` makes that distinction and nothing else decides it:

- **Items collected** — commit the bundle.
- **Channels read, nothing matched** — commit nothing, report it, succeed. A scheduled job that goes
  red because the world was calm is a job that gets muted, and then the real failures are muted too.
- **Nothing read at all** — commit nothing and fail the run.

An outcome the module does not recognise counts as *not read*. If `collect.py` grows a new way to
fail and this file is not updated, the conservative reading raises a false alarm that somebody fixes;
the optimistic reading hides a real one indefinitely. The self-test pins that default rather than the
list of outcomes, because the list is the thing that will drift.

### The lint moves in front of the commit

Every committed bundle is linted by the parser that reads it at runtime, inside the ordinary test
suite — ADR 025's "one gate that cannot be forgotten beats two that can". That remains true and is no
longer sufficient on its own. The build a malformed bundle would fail is the *next* one, by which
time the bad file is on `main` and the published page has stopped updating.

So the collection workflow runs the same lint before it commits, and commits nothing that does not
pass. It is the identical test, not a second copy of the rules; the bundles reach it through a glob
in the test project, so a file collected ten seconds earlier is checked with no wiring at all. Two
minutes a day buys the property that the automation cannot break the branch it writes to.

### The working set is pruned; the history is the archive

Daily collection across two briefs is some seven hundred files a year, every one of them parsed on
every poll and all but the last fortnight's skipped immediately. Unbounded accumulation is not
automation, it is manual work deferred.

Bundles older than twenty-eight days are removed from the working tree — twice the runtime's
fourteen, so pruning can never take something the pipeline was still reading, and raising
`MaxBundleAge` as far as twenty-eight days would not silently start deleting live data. Nothing is
destroyed: every bundle stays in the repository's history, which is where an archive belongs. A
bundle that cannot be parsed is never pruned, because an unreadable file is evidence of something
going wrong and deleting it on a schedule would remove exactly that.

### The commit carries the owner's name

A workflow that commits would ordinarily do so as `github-actions[bot]` at a `noreply` address. This
repository's attribution rule forbids that in as many words: the git author and committer are always
the repository owner, never a tool identity and never a `noreply` vendor address. The rule exists to
stop a second contributor appearing against work that is not theirs, and a bot account satisfies its
letter no better than an AI trailer does.

The automation is the owner's, it runs in the owner's repository, on briefs the owner committed. The
commit is authored accordingly, and its message says plainly that a scheduled round produced it — so
how the bundle was made is on the record without inventing an author to carry it.

### Deciding is a function; the shell only acts

`tools/collect.sh` runs the round and `tools/collect/schedule.py` makes every judgement in it, with
an offline self-test that `tools/verify.sh tooling` runs on every push. This is ADR 030's reasoning
applied to a second set of rules: *a brief is a document, and a page being read can argue with a
document.* So can a paragraph describing when a scheduled job should be believed.

The script is also what a developer runs — the workflow calls `tools/collect.sh` unchanged, for the
same reason `ci.yml` calls `tools/verify.sh` rather than repeating its checks. A scheduled job whose
logic exists only inside a workflow can be debugged only by pushing and waiting.

## What this does not automate

Stated because the gap is the interesting part, and because a reader could otherwise take "collection
is automated" to mean more than it does.

- **Judging what came back.** The round reports per-channel outcomes and refuses to commit nothing,
  and neither of those notices that a channel has quietly become worthless while still returning 200.
  A brief that has drifted out of usefulness looks, to this workflow, exactly like one that is
  working.
- **Extending the briefs.** Two briefs, roughly ten channels each, is the reach this project has. A
  schedule runs them more often; it does not widen them, and running a narrow brief daily produces a
  confident-looking series of bundles about the same small set of channels.
- **The credentialed datasets.** ACLED and UCDP remain the largest available increase in coverage and
  both need a human to request a key. Nothing here changes that.

## Consequences

- Collected material stops expiring. A round runs daily against a fourteen-day expiry, so several
  consecutive failures can pass before anything published goes stale.
- The failure mode that is actually dangerous — a collector that reaches nothing while reporting
  success — now fails the run loudly, and is the only condition that does.
- A quiet week commits nothing at all. The repository does not accumulate empty bundles recording
  that nothing happened, and `git log data/osint` is a record of rounds that found something.
- The published page updates from collection as well as from pushes, because the round asks the Pages
  workflow to run. A push made with `GITHUB_TOKEN` deliberately does not trigger other workflows, so
  this is asked for explicitly; if it ever stops working the step fails rather than leaving bundles
  committed and never published.
- Re-running a brief on the same day overwrites that day's bundle rather than adding one. One bundle
  per brief per day is the convention the filename already implied.
- Running `tools/collect.sh` locally can never push. It collects and prunes the working tree, and
  what reaches the repository is decided entirely in the workflow.
