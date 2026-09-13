# ADR 031: Coded datasets are read as archives, in bounded windows of time

- Status: Accepted
- Date: 2026-09-13
- Sprint 11 of [the global coverage assessment](../global-coverage-plan.md), which identified ACLED
  and UCDP as the single largest available increase in coverage and noted that both adapters already
  existed, disabled, waiting for a credential.
- Extends [ADR 003](003-processing-queue.md), whose bounded queue is exercised at a volume it had
  never seen. Leaves [ADR 005](005-location-resolution.md) and
  [ADR 021](021-outbound-trust-boundary.md) untouched.

## Context

The ACLED and UCDP adapters were written as if they were feeds. Each poll asked one open-ended
question — ACLED `event_date >= <a date>`, UCDP `StartDate=<a date>` with no end — took the first
few dozen rows of whatever came back, and discarded everything the response said about whether that
was the complete answer.

For an RSS feed that is right. A feed is a window onto the present and returns everything it has.
ACLED and UCDP are archives with years of human-coded conflict behind them, and three things follow
from that which had no analogue anywhere else in this system:

1. **An answer can be silently cut off.** A row limit is not an error. A response holding exactly as
   many rows as were asked for looks identical to a complete one, and treating it as complete means
   quietly dropping events from the busiest windows — precisely the windows that matter.
2. **History is the point.** "Every regional and local conflict" is not a claim about this week.
   Reaching back through an archive is a different ingestion shape from polling, and one that has to
   survive being stopped.
3. **Volume.** A global pull is orders of magnitude more than four RSS feeds.

## Decision

### Narrow by time, not by page number

Both adapters request a bounded date window. A window the provider could not answer in full is
halved and asked again, down to a floor of one day.

Paging was the obvious alternative and was rejected on what may be assumed. Page indexing is
off-by-one-able, and neither provider documents its base publicly enough to rely on. Getting it
wrong skips a page, and a silently skipped page is the worst failure available to this system:
nothing surfaces, and a map cannot show the events it was never offered. A date window is
unambiguous, both APIs document filtering on one with a worked example, and both are already pinned
by recorded fixtures. A short answer to a narrow question also proves its own completeness, which no
page number does.

The floor is one day because both APIs code to a calendar day. Narrowing past it would re-ask the
same question with different arithmetic.

### Each provider's own completeness signal, not a shared guess

UCDP states how many pages a query matched and links to the next one, so completeness is read rather
than inferred. ACLED publishes no such flag, so a response holding as many rows as the request
allowed is treated as possibly cut off.

The asymmetry is deliberate. A single shared heuristic would mean ignoring information UCDP gives us
for free, and the fixture pinning this is the case a row-count heuristic gets wrong: two rows
returned, and an envelope saying the query matched four hundred.

UCDP's next-page link is read as a flag and **never dialled**. A URL supplied in a response is a fact
about the response, not an instruction — the same reasoning that has redirects judged against the
resolved address at connection time rather than trusted from the payload.

### A window still truncated at the floor is a recorded gap

It is logged as incomplete, and the rows that did arrive are kept. Half of a busy day is worth more
than none of it, and the overlap between a window and its halves costs nothing: repeats are
suppressed within a run and rejected by the fingerprint index against the database.

### Backfill is bounded by a request budget and resumed from a checkpoint

History is walked backwards one window at a time. Each poll gets a fixed number of requests, shared
with the live window and spent on it first — a busy day now outranks a quiet week in 2019 — and stops
mid-walk without apology when the budget runs out.

**The checkpoint is stored, not derived.** The oldest record held from a source looks like it would
say the same thing for free. It would not. "The earliest event we hold" and "the earliest date we
have asked about" are different facts that come apart exactly where it matters: a quiet fortnight in
a small country returns no events, the oldest stored record does not move, and a walk driven from it
would re-request that fortnight on every poll forever without reaching the month before it. The
checkpoint records requests; the observations record findings; neither derives from the other.

**A window left half-read never advances the checkpoint.** Advancing past it would skip the remainder
permanently, and the gap would be invisible.

Backfill is off unless a deployment names a date. A clone that acquires a credential reads the
present; pulling a decade out of someone else's API is a decision somebody has to make on purpose.

### The export drains its queue while filling it

The snapshot exporter filled the bounded queue to completion and only then drained it. That works
for exactly as long as a run fits inside the queue — four RSS feeds do, a dataset adapter does not —
and past that point it deadlocks permanently on a producer with no consumer. The consumer now starts
first.

Still a single consumer. Correlation depends on arrival order, so a concurrent drain would make
identical input publish different snapshots, and a reproducible build is worth more here than
throughput.

## Consequences

The adapters can be pointed at any span of either archive, and told how much of one poll to spend
doing it. What they cannot do is silently return less than they were asked for.

The published dashboard gets the live window only, and says so. Its export runs against a throwaway
database, so a checkpoint cannot survive it; backfill belongs to a deployment that keeps its data.
See [the operator notes](../operations/dataset-credentials.md).

A new `IngestionCheckpoints` table holds one row per source. It is in the infrastructure project
rather than the domain, because it records nothing about the world — it records what a process has
asked for.

Turning either adapter on remains one credential, held in deployment secrets, never committed.

What this does **not** address is placement. An event neither dataset can be drawn for is an event
this system stores, classifies and cannot map, and that constraint is the gazetteer's rather than the
sources'. It is Sprint 10's, and it is the larger piece of work.
