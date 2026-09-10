# Architecture

## Boundary model

```text
                  +------------------------+
                  | API / Workers          |
                  | HTTP, SignalR,         |
                  | hosting, composition   |
                  +-----------+------------+
                              |
                  +-----------v------------+
                  | Infrastructure         |
                  | EF Core, SQLite, queue,|
                  | sources, gazetteer     |
                  +-----------+------------+
                              |
                  +-----------v------------+
                  | Application            |
                  | pipeline, use cases,   |
                  | contracts, abstractions|
                  +-----------+------------+
                              |
                  +-----------v------------+
                  | Domain                 |
                  | incidents, observations|
                  | locations, invariants  |
                  +------------------------+
```

## Processing pipeline

```text
IEventSource (replay, manual submission)
      |
      v
ObservationIngestionService   validation; untrusted input stops here
      |
      v
ChannelObservationBuffer      bounded queue, backpressure  (ADR 003)
      |
      v
ObservationProcessorService   N workers, one DI scope per item
      |
      v
ObservationProcessor
      |-- normalise            deterministic; source-declared fields win
      |-- deduplicate          content fingerprint + unique index
      |-- resolve location     deterministic resolver only          (ADR 005)
      |-- correlate            category + time + distance           (ADR 006)
      |-- persist              observation and incident in one save
      +-- publish              SignalR, after commit, best effort   (ADR 008)
```

### Why the stages are in this order

**Deduplication precedes enrichment.** Enrichment is the expensive, fallible part of the pipeline.
Rejecting a re-delivery before that point avoids paying for work whose result is discarded.

**Location resolution precedes correlation.** Distance is the correlator's strongest signal, so
coordinates must exist before candidates are scored. Without them the correlator falls back to
matching place names and reports lower confidence.

**Persistence precedes publication.** SignalR is a projection of committed state. A client that
misses a message recovers by re-querying the API; a client that receives a message about state that
was never committed cannot recover at all.

## Failure behaviour

The pipeline is built so that a failure degrades coverage rather than destroying evidence.

| Failure | Effect |
| --- | --- |
| Location resolver unavailable or unable to match | Observation is persisted without coordinates and marked unresolved. It is never given invented coordinates. |
| One ingestion source throws | That source stops; other sources and the processor continue. |
| Correlation or persistence throws | The observation is retained with `Status = Failed` and a reason, so it can be diagnosed and reprocessed. |
| SignalR publication throws | Logged and counted. Committed state is unaffected; clients recover on refresh or reconnect. |
| Two workers process the same payload | A filtered unique index on the fingerprint rejects the second write, which is handled as a duplicate. |
| Host shuts down mid-item | Cancellation propagates; the item is not written as a spurious failure and can be redelivered. |

An observation is retained in every one of these cases except deliberate cancellation. Duplicates
and failures are kept rather than discarded, because "this was reported twice" and "this could not
be processed" are both facts worth auditing.

## Trust boundaries

- Ingestion adapters, manual submissions, and any future model output are untrusted until validated.
- Coordinates come only from a structured provider's own record or the local gazetteer. Free text is
  never turned into coordinates by inference. This constraint is what ADR 005 exists to protect.
- Demo and replay records are labelled at the source and stay labelled through the API and the UI.
- The Application layer defines repository behaviour; EF Core stays an Infrastructure concern, and
  provider-specific exceptions are translated at that boundary rather than leaking upward.

## Observability

`PipelineDiagnostics` owns one meter (`Geopolitics.Pipeline`) and one activity source of the same
name. Counters cover ingestion, processing, deduplication, incident creation and correlation,
geocoding outcomes, and realtime publication; a histogram records end-to-end processing time tagged
by outcome. Each processed item opens an activity carrying its source, identifier, and fingerprint,
so a single observation can be followed from ingestion through to delivery.

## Hosting topology

Both hosts compose the same pipeline from the same registration methods.

- **`Geopolitics.Api`** serves the dashboard and API, runs the pipeline, and registers the SignalR
  notifier.
- **`Geopolitics.Workers`** runs the pipeline headless with no realtime layer. It exists to
  demonstrate that processing has no dependency on SignalR or on a connected browser.

`Pipeline:SourcesEnabled` and `Pipeline:ProcessorEnabled` control which host does which job, so
ingestion and processing can be separated without code changes.
