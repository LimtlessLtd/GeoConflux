# GeoConflux

GeoConflux is a .NET 10 modular-monolith geopolitical intelligence platform. It distinguishes source
observations from the correlated incidents shown to users, and is designed to demonstrate a
trustworthy asynchronous processing pipeline rather than just a map.

**Live demo: https://limtlessltd.github.io/GeoConflux/**

> The dashboard runs on **synthetic replay data**. It is labelled as demo data in the API, in the
> exported snapshot, and in the UI. It is not live reporting and describes no real-world events.

## Two ways to see it

**The published page** at [limtlessltd.github.io/GeoConflux](https://limtlessltd.github.io/GeoConflux/)
is a *static snapshot*. GitHub Pages serves files only, so no .NET process, database, or SignalR hub
runs there. What you see was produced by a genuine run of the pipeline during the build and exported
to JSON — the same queue, deduplicator, gazetteer, and correlator the application uses. The globe,
filters, incident drawer, and source evidence all work, and the **Replay the recorded run** button in
the Feed tab plays the observations back in order so you can watch incidents form and correlate.

**Running it locally** starts the real thing: the bounded queue, background workers, SQLite
persistence, and live SignalR updates.

```powershell
dotnet run --project src/Geopolitics.Api
```

## What works today

Sprints 1 to 3 are complete. The application ingests a recorded observation stream, enriches each
item through a schema-validated AI stage, processes it asynchronously, and streams results to the
dashboard in realtime.

```text
IEventSource -> validation -> bounded Channel -> background processor
             -> normalise -> deduplicate -> AI enrich -> validate
             -> resolve location -> correlate -> persist -> SignalR -> dashboard
```

Concretely, running the app locally will:

- ingest eight recorded observations from four synthetic sources, with realistic arrival delays;
- reject one byte-identical redelivery as a duplicate, while keeping it for audit, and skip
  enrichment for it rather than paying for work about to be discarded;
- send every other observation through enrichment, validate the response against a versioned schema,
  and record the attempt — success or failure — as an auditable inference;
- correlate two differently-worded reports of the same event into a single incident;
- place observations using provider coordinates or a local gazetteer, and leave one deliberately
  unmappable report visible without coordinates;
- show a confidence score and the method that produced it beside every classification;
- push each result to the browser over SignalR with no page refresh.

You can also submit your own observation from the **Submit** tab and watch it go through the same
pipeline.

## The AI stage

Enrichment translates, summarises, classifies, assesses severity, extracts named actors, and
**names** a place. The provider is chosen by configuration through `Microsoft.Extensions.AI`:

| `Ai:Provider` | Needs | Notes |
| --- | --- | --- |
| `Mock` *(default)* | nothing | Deterministic in-process stand-in. **Not a language model.** |
| `Ollama` | a local daemon | Reached through its OpenAI-compatible endpoint |
| `OpenAI` | `Ai:ApiKey` | |
| `AzureOpenAI` | `Ai:Endpoint`, `Ai:ApiKey` | |

Three things are true of every path through that stage:

1. **Model output is untrusted input.** The response is validated against a versioned schema --
   enum vocabulary, bounds, counts, control characters, unmapped properties. All errors are
   collected and handed back in a single repair turn; one repair is allowed. What gets persisted is
   a re-serialisation of the *validated projection*, so no reasoning transcript or surprise field
   can reach the database ([ADR 012](docs/adr/012-ai-output-is-untrusted-input.md)).
2. **A model can name a place but never position one.** The response schema has no latitude or
   longitude field at all, so the constraint is structural rather than a rule someone has to
   remember ([ADR 005](docs/adr/005-location-resolution.md)).
3. **Failure degrades quality, never evidence.** A provider that is down, slow, or wrong leaves the
   deterministic keyword classification in place, and the failure is recorded rather than hidden.

Every attempt writes an `AiInference` row — provider, model, prompt version, schema version,
outcome, confidence, attempt count, latency — so any classification on the dashboard is traceable
to what produced it.

### Running with no credentials

The default provider is a deterministic stand-in that runs in-process. It is a test double, not a
small model, and it is built so it cannot be mistaken for one: it labels itself
`ai:Mock/deterministic-stub` everywhere, and it **does not invent translations**. Given text in a
script it cannot read, it says so and reports what it can establish instead
([ADR 013](docs/adr/013-deterministic-provider-by-default.md)).

The value of it is that the offline path is the *real* path — same prompt, schema, validator,
repair loop, telemetry, and audit record. Only the responder differs.

It also makes the geolocation rule visible without a credential. Submit an Arabic report mentioning
Bab-el-Mandeb and the stand-in reports the language, *names* the place, and honestly declines to
classify text it cannot read — so the observation is placed at 12.585, 43.334 by the gazetteer while
its category still says `keyword · 20%`. Naming and positioning are separate steps, and you can watch
them be separate.

## AI evaluation

There is an evaluation harness rather than an assumption that the model works. It runs the real
enrichment service over 16 labelled fixtures and scores classification, severity, language,
location naming, and entity extraction with per-class precision, recall, and F1.

**Every number below was produced by `dotnet test` and written by the harness.** It measures the
*offline baseline* — the keyword classifier and script detector — because that is the default
provider. It is not a measurement of any language model.

| Measure | Value |
| --- | ---: |
| Structured output success rate | 100.0 % |
| Language accuracy | 1.00 |
| Event type — accuracy / macro F1 | 0.62 / 0.68 |
| Severity — accuracy / macro F1 | 0.75 / 0.74 |
| Location name — precision / recall / F1 | 1.00 / 0.75 / 0.86 |
| Entities — precision / recall / F1 | 0.10 / 0.67 / 0.17 |

The entity figure is poor because the stand-in finds capitalised runs, which recovers most of the
right names and a lot of noise. Location recall is 0.75 because the baseline cannot read the Arabic,
Russian, and Chinese fixtures. Both are reported rather than tuned away: they are the gap a real
model is expected to close.

Sixteen synthetic, author-labelled cases cannot support a claim about geopolitical classification
ability. The set exists to catch regressions. Full method, per-class tables, and limitations:
[tests/data/ai-evaluation/](tests/data/ai-evaluation/) and the generated
[RESULTS.md](tests/data/ai-evaluation/RESULTS.md).

To measure a real model instead, set `GEOCONFLUX_EVAL_PROVIDER` and `GEOCONFLUX_EVAL_MODEL` and
rerun `dotnet test tests/Geopolitics.AiEvaluationTests`.

## Design decisions worth reading

- **An observation is not an incident.** Several sources reporting one event produce one incident
  with several pieces of linked evidence. Duplicate evidence is linked, not deleted ([ADR 006](docs/adr/006-event-correlation.md)).
- **A language model may never set coordinates.** Only a deterministic resolver produces latitude and
  longitude. An unmappable report stays unplaced rather than being given plausible-looking
  coordinates ([ADR 005](docs/adr/005-location-resolution.md)).
- **SignalR is not the processing backbone.** Publication happens after a successful save, and the
  pipeline runs correctly with zero connected clients ([ADR 008](docs/adr/008-signalr.md)).
- **Evidence survives failure.** A failed enrichment, geocode, or correlation retains the source
  payload with a recorded reason instead of discarding it.
- **Model output is untrusted input.** It is schema-validated, bounded, and given exactly one repair
  attempt before the deterministic classifier takes over
  ([ADR 012](docs/adr/012-ai-output-is-untrusted-input.md)).
- **No classification is shown without its confidence and method.** A category on its own reads as a
  fact; "HIGH, 45%, keyword match" does not. Both are persisted on the observation and the incident,
  not computed for display.
- **No ML model is used yet.** Severity comes from enrichment or from keywords. A trained model and
  the LLM-versus-ML-versus-label comparison are a later increment
  ([ADR 009](docs/adr/009-ml-model.md)).

See [docs/architecture.md](docs/architecture.md) for the stage ordering and failure behaviour.

## Run locally

Install .NET SDK 10.0.401 or a compatible .NET 10 feature band, then run:

```powershell
dotnet restore GeopoliticsDashboard.sln
dotnet run --project src/Geopolitics.Api
```

Open the URL printed by ASP.NET. The replay stream begins immediately; watch the **Live feed** tab to
see observations arrive and the globe update without a refresh.

No credentials of any kind are required — the default AI provider runs in-process. To use a real
model, set `Ai:Provider` and supply `Ai:ApiKey` through environment variables or user secrets. No
credential is ever read from a committed file.

### Endpoints

| Endpoint | Purpose |
| --- | --- |
| `GET /api/incidents` | Correlated incidents, newest first |
| `GET /api/incidents/{id}` | One incident |
| `GET /api/observations` | Recent observations, including duplicates and failures |
| `GET /api/observations/by-incident/{id}` | Evidence linked to one incident |
| `POST /api/observations` | Queue a manual observation (returns `202 Accepted`) |
| `GET /api/health` | Health, including processing-queue depth and saturation |
| `/hubs/incidents` | SignalR hub for realtime updates |
| `GET /openapi/v1.json` | OpenAPI document |

### Configuration

| Setting | Default | Purpose |
| --- | --- | --- |
| `Pipeline:QueueCapacity` | 512 | Queue depth before producers are made to wait |
| `Pipeline:ProcessorConcurrency` | 2 | Concurrent processing workers |
| `Pipeline:CorrelationWindow` | 24:00:00 | How far back the correlator looks |
| `Pipeline:CorrelationRadiusKilometres` | 75 | How close two reports must be to be the same event |
| `Pipeline:SourcesEnabled` | true | Whether this host runs ingestion sources |
| `Pipeline:ProcessorEnabled` | true | Whether this host drains the queue |
| `Enrichment:Enabled` | true | Whether observations are sent for enrichment at all |
| `Enrichment:Timeout` | 00:00:20 | Ceiling on one enrichment attempt, including any repair |
| `Enrichment:MaxRepairAttempts` | 1 | Extra calls allowed to correct output that failed validation |
| `Enrichment:MinimumAcceptedConfidence` | 0.35 | Below this, the inference is recorded but not applied |
| `Ai:Provider` | Mock | `Mock`, `Ollama`, `OpenAI`, or `AzureOpenAI` |
| `Ai:Model` | llama3.2 | Model or deployment name |
| `Ai:Endpoint` | none | Required for `AzureOpenAI`; defaults to the local daemon for `Ollama` |
| `Ai:ApiKey` | none | **Never put this in a file.** Use environment variables or user secrets. |
| `Replay:Enabled` | true | Whether the recorded demo stream runs |
| `Replay:SpeedFactor` | 1 | Multiplier on recorded delays; `0` removes them |
| `Replay:Loop` | false | Restart the recorded stream for an unattended demo |

To run the pipeline headless, with no realtime layer at all:

```powershell
dotnet run --project src/Geopolitics.Workers
```

## Publishing

`.github/workflows/pages.yml` builds the solution, runs the tests, executes the pipeline, exports its
output, and deploys the result to GitHub Pages on every push to `main` and weekly on a schedule. The
weekly rebuild exists because the snapshot records when it was generated and the dashboard displays
that date, so a scheduled run keeps the published run recent rather than visibly stale.

To produce a snapshot yourself:

```powershell
dotnet build GeopoliticsDashboard.sln --configuration Release
mkdir dist; Copy-Item -Recurse src/Geopolitics.Api/wwwroot/* dist/
dotnet run --project src/Geopolitics.Workers --configuration Release --no-build — --export "$PWD/dist/data"
```

Pass `--export` an **absolute** path. `dotnet run` executes the program with the project directory as
its working directory, so a relative path writes the snapshot somewhere surprising.

Serving `dist/` with any static file server reproduces the published page.

## Verification

```powershell
dotnet build GeopoliticsDashboard.sln
dotnet test GeopoliticsDashboard.sln
dotnet format GeopoliticsDashboard.sln --verify-no-changes
docker build -t geopolitics-dashboard .
```

127 tests cover domain invariants, fingerprinting, classification, correlation scoring, queue
backpressure and cancellation, gazetteer resolution, and the processor's failure paths; the AI trust
boundary (malformed JSON, unknown enums, out-of-range confidence, oversized payloads, control
characters, prompt-injection fixtures, provider timeout, provider exception, repair success and
exhaustion); end-to-end integration tests that drive the real host and assert on what the API then
serves; and the evaluation harness above.

## Not yet implemented

There is no live external feed, no spatial querying, no analytics, and no ML model yet. Those arrive
in Sprints 4 to 6 and are deliberately not represented as working before then.

Two limitations worth stating plainly:

- **The default AI provider is a deterministic stand-in, not a language model.** Everything it
  produces is labelled as such. The published snapshot was built with it.
- **Correlation has a read-then-write race** when more than one worker runs and two reports of the
  same event arrive simultaneously; each can open an incident. It does not occur at realistic
  arrival rates, deduplication is unaffected, and the fix belongs with the Sprint 4 correlation
  work. See [docs/architecture.md](docs/architecture.md#known-limitations).

See the [ADRs](docs/adr/) and the authoritative [project plan](GeoConflux_Plan.md).
