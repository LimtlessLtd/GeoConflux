# ADR 023: A failed commit keeps the evidence and discards the derived state

- Status: Accepted
- Date: 2026-09-12
- Refines [ADR 003](003-processing-queue.md) and [ADR 010](010-observation-deduplication.md), whose
  guarantees this decision is what actually delivers on the failure paths.

## Context

The pipeline commits an observation, the incident it correlated with, and the link between them in a
single unit of work. That is deliberate and stays: nothing is announced to clients until all three
are stored together.

The final architecture review asked what happens when that commit fails, and found the answer was
incoherent in two directions.

**Recovery re-committed what it was recovering from.** After a failure the processor keeps the source
payload so the report can be diagnosed and reprocessed. It did that by marking the observation failed
and calling save again — on the same context, which still held every pending change from the attempt
that had just failed. EF Core does not roll back the change tracker on a failed save, so this was not
storing one row. It was retrying the whole failed unit of work with one status flipped, and when the
original fault was transient it succeeded: an incident, committed, assembled entirely from an
observation the pipeline was in the middle of reporting as failed and would never announce.

**A lost deduplication race was not recognised as one.** The translation from a unique-index violation
to `DuplicateObservationException` lived on the observation repository, but the commit that inserts an
observation in the ordinary path is issued through the incident repository. So the loser of a race
raised a raw provider exception, was counted and logged as a broken observation, and then had its
payload discarded when the retry hit the same index. Both hosts ship `ProcessorConcurrency: 2`, so
this was reachable in the default configuration.

## Decision

### Duplicate detection is a property of the context, not of a repository

`GeopoliticsDbContext.SaveChangesAsync` translates the fingerprint conflict. Every save through the
context gets it, whichever repository issued the call, including any repository added later.

The alternative — repeating the `catch` in the incident repository — would have fixed the symptom and
left the same trap for the third repository. Where a guarantee is a property of the database, it
belongs on the thing that talks to the database.

### Recovery keeps evidence and drops derived state

`IObservationRepository.RetainEvidenceAsync` detaches the tracked incidents and commits what remains.
Both recovery paths use it: the one that retains a failed observation, and the one that retains the
loser of a commit race.

The distinction it draws is the decision. **Evidence** is what a source actually gave us — the
observation, and the audit record of any enrichment attempt made against it. **Derived state** is what
this system concluded from it — the incident, its correlation, its linkage. When an attempt fails, the
evidence is exactly what a person needs in order to work out why, and the conclusion is precisely what
should not survive, because the pipeline has just said it does not stand behind it.

Enrichment audit rows are therefore kept rather than cleared, which is why this detaches incidents
specifically instead of clearing the change tracker wholesale. Those rows carry no foreign key to
observations on purpose, so that the record of having called a model outlives the observation it
describes; a recovery that dropped them would reverse that on the one path where it matters most. The
first draft of this change did clear the tracker, and that is what the test now pins.

### A concurrent duplicate is a duplicate

The losing delivery is stored, marked as a duplicate of the winner, exactly as a duplicate caught by
the read-then-insert check is. Whether a repeat is spotted by the read or by the index is a timing
accident, and it should not decide whether the delivery remains auditable afterwards. The filtered
unique index already exempts rows marked as duplicates, so the schema permitted this before the code
did.

`RawObservation.MarkDuplicate` clears `IncidentId` as part of this. A repeat delivery is evidence of
nothing and belongs to no incident; usually that is already true, and after a lost race it is not,
because the observation has been linked to an incident that rolled back with the failed save.

## Consequences

- A concurrent redelivery is reported as `Duplicate`, counted in `events.deduplicated`, logged at
  debug, and stored. It used to be reported as `Failed`, counted in `pipeline.items.failed`, logged
  as an error, and lost.
- A failed commit leaves the observation and its enrichment audit trail in the database and no
  incident. The dashboard cannot show a record the pipeline does not stand behind.
- The change tracker is manipulated directly in one place, inside the repository. That is a
  persistence-layer concern and it stays there; the processor expresses intent
  (`RetainEvidenceAsync`) and does not know EF exists.
- Both behaviours are covered by integration tests against real SQLite rather than fakes. Neither is
  reproducible in memory: one needs the filtered unique index, the other needs a change tracker that
  survives a failed save. The in-memory fakes were taught the same semantics so they cannot be more
  forgiving than the database.
- This is still an in-process guarantee. Two processor hosts against one database would not race on
  correctness — the unique index is authoritative for both — but the correlation gate above it is not
  distributed, which [ADR 016](016-correlation-signals-and-ordering.md) already records.
