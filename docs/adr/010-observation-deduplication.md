# ADR 010: Deduplicate on a content fingerprint enforced by a unique index

- Status: Accepted
- Date: 2026-09-10

## Context

Sources redeliver. A wire service reposts the same story, a poller overlaps its own window, and a
retry re-sends a payload that already succeeded. Sprint 2 needed a deduplication rule that works
offline, is reproducible in tests, and does not depend on a model.

Deduplication also has to survive concurrency. The processor runs several workers against one queue,
so two workers can hold identical payloads at the same time and both pass a read-then-insert check.

Separately, deduplication must not be confused with correlation. Two outlets describing the same
event in different words are *not* duplicates; they are corroborating evidence for one incident.

## Decision

Compute a SHA-256 fingerprint over the source name plus either the source-assigned identifier or, in
its absence, the content normalised for case, whitespace, and punctuation. A source-assigned
identifier takes precedence because it is the strongest signal the provider offers about its own
record's identity.

Enforce uniqueness with a filtered unique index (`Status <> 'Duplicate'`) rather than relying on the
application check alone. The repository translates the resulting constraint violation into a
`DuplicateObservationException`, which the processor handles as a duplicate.

Retain duplicates as rows with `Status = Duplicate` and a reference to the observation they repeat.

## Consequences

- Redeliveries are rejected deterministically with no external dependency.
- The concurrency race is closed by the database, which is the only component in a position to
  arbitrate it, and the application check remains as a cheap fast path.
- A source that revises a story under the same identifier is treated as the same report. This is a
  deliberate trade: it prevents minor edits from multiplying incidents, at the cost of not
  automatically capturing substantive rewrites. Correlation, not deduplication, is what links a
  genuinely new report to an existing incident.
- Duplicate deliveries stay auditable, which makes source reliability measurable later rather than
  invisible.
- Because the index is filtered, retained duplicate rows do not themselves collide.
