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
                  | sources, gazetteer,    |
                  | AI providers           |
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
      |-- deduplicate          content fingerprint + unique index   (ADR 010)
      |-- enrich               AI; validated, fallible, optional    (ADR 012)
      |-- resolve location     deterministic resolver only          (ADR 005)
      |-- correlate            category + time + distance           (ADR 006)
      |-- persist              observation, incident, inference     (one save)
      +-- publish              SignalR, after commit, best effort   (ADR 008)
```

## AI enrichment

```text
                observation text (untrusted)
                            |
                            v
              EnrichmentPrompt  v1, delimited, "this is data"
                            |
                            v
        IChatClient  <- Mock | Ollama | OpenAI | AzureOpenAI   (ADR 013)
                            |            selected by Ai:Provider
                            v
              EnrichmentPayloadValidator                        (ADR 012)
                 |                        |
          all errors collected         valid
                 |                        |
                 v                        v
       one repair turn ---------> ValidatedEnrichment
                 |                        |
            still invalid                 | confidence >= threshold?
                 |                   yes  |            | no
                 v                        v            v
       keyword classification    adopt enrichment   keep keyword
                 |                        |            |
                 +------------+-----------+------------+
                              v
                    AiInference row written either way
                    provider, model, prompt v, schema v,
                    outcome, confidence, attempts, latency
```

The model names a place. It cannot position one: the response schema has no latitude or longitude
field, so the deterministic resolver is the only path to coordinates (ADR 005). What is persisted is
a re-serialisation of the validated projection, so no chain-of-thought or unexpected field can
reach the database.

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
| AI provider unreachable, erroring, or timing out | The keyword classification stands. An `AiInference` row records the failure. The observation is persisted normally. |
| AI output fails schema validation twice | Same as above, with outcome `ValidationFailed` and the collected errors recorded. |
| AI reports confidence below the threshold | The inference is stored but not applied; the transparent heuristic is kept rather than replaced by an unsure guess. |
| The inference audit row cannot be written | Logged; processing continues. Losing telemetry is a smaller loss than losing evidence. |
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

- Ingestion adapters, manual submissions, and model output are all untrusted until validated. Model
  output in particular is validated against a versioned schema before any of it is adopted (ADR 012).
- Coordinates come only from a structured provider's own record or the local gazetteer. Free text is
  never turned into coordinates by inference. This constraint is what ADR 005 exists to protect.
- Demo and replay records are labelled at the source and stay labelled through the API and the UI.
- The Application layer defines repository behaviour; EF Core stays an Infrastructure concern, and
  provider-specific exceptions are translated at that boundary rather than leaking upward.

## Observability

`PipelineDiagnostics` owns one meter (`Geopolitics.Pipeline`) and one activity source of the same
name. Counters cover ingestion, processing, deduplication, incident creation and correlation,
geocoding outcomes, AI requests, failures, validation failures and repair attempts, and realtime
publication; histograms record end-to-end processing time tagged by outcome and per-attempt AI
latency tagged by provider. `Microsoft.Extensions.AI`'s own OpenTelemetry instrumentation is attached
to the chat client under the `Geopolitics.Ai` activity source. Each processed item opens an activity carrying its source, identifier, and fingerprint,
so a single observation can be followed from ingestion through to delivery.

## Hosting topology

Both hosts compose the same pipeline from the same registration methods.

- **`Geopolitics.Api`** serves the dashboard and API, runs the pipeline, and registers the SignalR
  notifier.
- **`Geopolitics.Workers`** runs the pipeline headless with no realtime layer. It exists to
  demonstrate that processing has no dependency on SignalR or on a connected browser.

`Pipeline:SourcesEnabled` and `Pipeline:ProcessorEnabled` control which host does which job, so
ingestion and processing can be separated without code changes.

## Known limitations

Recorded here rather than discovered later.

**Correlation has a read-then-write race.** `ListCorrelationCandidatesAsync` reads candidates and the
processor writes an incident without holding a lock across the two. With
`Pipeline:ProcessorConcurrency > 1`, two reports of the same event arriving simultaneously can each
open an incident, because neither sees the other's uncommitted write. It is visible when the replay
stream is driven with `Replay:SpeedFactor = 0` and two workers, and does not occur at realistic
arrival rates. Deduplication is unaffected — that guarantee rests on a unique index, not on a read.
A durable fix needs either a correlation-scoped lock or a post-commit merge step, and belongs with
the Sprint 4 correlation work rather than being bolted on here.

**The gazetteer is small and Latin-script.** It holds the chokepoints, seas, and cities that recur in
the demo dataset. A place outside it resolves to nothing, and the observation stays visibly unplaced.
That is the designed behaviour, but it means location recall is bounded by the lexicon rather than by
the extraction step.

**No classical ML model exists yet.** Severity comes from enrichment or from keywords. The comparison
between a model prediction, an LLM assessment, and a human label is a later increment (ADR 009).
