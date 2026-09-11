# GeoConflux

GeoConflux is a .NET 10 modular-monolith geopolitical intelligence platform. It distinguishes source
observations from the correlated incidents shown to users, and is designed to demonstrate a
trustworthy asynchronous processing pipeline rather than just a map.

**Live dashboard: [limtlessltd.github.io/GeoConflux](https://limtlessltd.github.io/GeoConflux/)**

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

Sprints 1 to 4 are complete, and Sprint 5 has begun with the analytics layer. The application
ingests a recorded observation stream, enriches each item through a schema-validated AI stage,
processes it asynchronously, streams results to the dashboard in realtime, and summarises what it
has stored across four time windows. Adapters for RSS, NASA FIRMS, and ACLED feed the same pipeline
when they are configured; all three ship disabled.

```text
IEventSource -> validation -> bounded Channel -> background processor
(replay, RSS,    -> normalise -> deduplicate -> AI enrich -> validate
 FIRMS, ACLED,   -> resolve location -> correlate -> persist -> SignalR -> dashboard
 manual)
```

Concretely, running the app locally will:

- ingest eleven recorded observations from five synthetic sources, with realistic arrival delays;
- reject one byte-identical redelivery as a duplicate, while keeping it for audit, and skip
  enrichment for it rather than paying for work about to be discarded;
- send every other observation through enrichment, validate the response against a versioned schema,
  and record the attempt — success or failure — as an auditable inference;
- correlate three differently-worded reports of one event — from two news outlets and a structured
  event database — into a single incident, scoring distance, shared actors, and shared wording;
- link two reports that name the same actor in near-identical words even though neither can be
  placed, which is the only signal available when a report states no location;
- place observations using provider coordinates or a local gazetteer, and leave one deliberately
  unmappable report visible without coordinates;
- show a confidence score and the method that produced it beside every classification;
- push each result to the browser over SignalR with no page refresh;
- report what was recorded near each watched maritime chokepoint, with measured distances, in the
  **Chokepoints** tab;
- summarise the last 24 hours, 7, 30, or 90 days in the **Analytics** tab — severity and event-type
  distributions, regional activity, where the evidence came from, what the pipeline did with it,
  incidents over time, and a documented activity score shown with the formula that produced it.

You can also submit your own observation from the **Submit** tab and watch it go through the same
pipeline.

## Live OSINT providers

Three live adapters exist alongside the recorded replay stream. Every one of them is **off** in the
configuration committed here, and a test asserts that under this configuration the application makes
no outbound HTTP request at all.

| Provider | Source name | Needs | Declares |
| --- | --- | --- | --- |
| RSS / Atom | `rss:<feed>` | feed URLs | nothing |
| NASA FIRMS | `firms:<dataset>` | `Providers:NasaFirms:ApiKey` | coordinates, `NaturalHazard`, low severity |
| ACLED | `acled` | `Providers:Acled:ApiKey` + `:Email` | coordinates, category, fatality-derived severity |

A provider polls only when `Providers:Mode` is `Live` **and** its own `Enabled` is `true`. One switch
would be too easy to flip by copying an example config into a demo deployment.

What each adapter is allowed to declare follows from what its provider actually knows:

- **FIRMS measures position.** A satellite geolocating a thermal pixel is a measurement, so its
  coordinates are authoritative. It is also the one source whose detections are *not* geopolitical:
  most thermal anomalies are agricultural burning or wildfire, so detections enter as
  `NaturalHazard` at low severity and their text says that a thermal detection records heat, not its
  cause. Their value is corroborative — a signature near an incident other sources are reporting.
- **ACLED codes by hand.** Its category and coordinates are stated, and severity comes from its own
  fatality count by a stated rule, not from a model reading the notes.
- **RSS is prose.** It declares nothing. An article earns a map position only by naming a place the
  gazetteer recognises, exactly as a manual submission does.

Enabling one, as environment variables:

```powershell
$env:Providers__Mode = "Live"
$env:Providers__Rss__Enabled = "true"
$env:Providers__Rss__Feeds__0__Name = "world"
$env:Providers__Rss__Feeds__0__Url = "https://example.com/feed.xml"
dotnet run --project src/Geopolitics.Api
```

Credentials go in environment variables or user secrets. None is committed, and none is needed to run
or test the project.

**These adapters have not been run against the live services.** They are verified against recorded
payloads matching each provider's documented response shape — including malformed bodies, a rate
limit, a server outage, and a rejected credential — driven through the application's own HTTP stack.
That is what can be verified without a credential, and it is not the same claim as having polled the
real endpoints.

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
- **Correlation needs positional evidence, never time and topic alone.** What establishes that two
  reports concern the same place sets a ceiling on confidence — measured distance highest, a shared
  place name lower — and time, shared actors, and shared wording scale it from there. A wrong merge
  destroys the distinction between two real events and is nearly invisible afterwards; two incidents
  that should have been one are obvious on the map, so the bar errs high
  ([ADR 016](docs/adr/016-correlation-signals-and-ordering.md)).
- **Spatial search narrows with an index, then measures exactly.** A search circle becomes the
  rectangle that contains it, which the database can serve from an index on coordinates; every row it
  admits is then measured with a great-circle distance, because a rectangle's corners reach about 1.4
  times the radius. SpatiaLite was evaluated and rejected on measured grounds — its native library
  ships for Windows only in the NuGet package, and this project builds and publishes on Linux
  ([ADR 017](docs/adr/017-spatial-querying.md)).
- **Similarity is lexical, and says so.** The default measure compares the words two reports share
  and reports its method as `lexical-overlap`. It is not an embedding model and is not described as
  one; `ITextSimilarity` is the seam for a real one.
- **A language model may never set coordinates.** Only a deterministic resolver produces latitude and
  longitude. An unmappable report stays unplaced rather than being given plausible-looking
  coordinates ([ADR 005](docs/adr/005-location-resolution.md)).
- **SignalR is not the processing backbone.** Publication happens after a successful save, and the
  pipeline runs correctly with zero connected clients ([ADR 008](docs/adr/008-signalr.md)).
- **The globe shows real imagery without a credential.** Esri's public map services supply satellite,
  street, and topographic basemaps down to building level, with borders and place names layered on
  the satellite view. If the tile host is unreachable the globe falls back to the texture bundled
  with CesiumJS and says so, rather than going blank ([ADR 014](docs/adr/014-basemap-imagery.md)).
- **An external integration fails in its own blast radius.** Timeout, exponential backoff with
  jitter, `Retry-After`, and a circuit breaker come from the standard .NET resilience handler; a
  4xx that means "your request is wrong" is not retried, so a bad credential fails once rather than
  four times. A provider that is down, slow, rate-limited, or returning nonsense costs its own poll
  and nothing else ([ADR 015](docs/adr/015-live-provider-ingestion.md)).
- **Evidence survives failure.** A failed enrichment, geocode, or correlation retains the source
  payload with a recorded reason instead of discarding it.
- **Model output is untrusted input.** It is schema-validated, bounded, and given exactly one repair
  attempt before the deterministic classifier takes over
  ([ADR 012](docs/adr/012-ai-output-is-untrusted-input.md)).
- **No classification is shown without its confidence and method.** A category on its own reads as a
  fact; "HIGH, 45%, keyword match" does not. Both are persisted on the observation and the incident,
  not computed for display.
- **The activity score is a heuristic, and cannot be quoted without saying so.** Each incident
  contributes severity x recency x corroboration x confidence; the total is divided by the window
  length to give a rate, and that rate is mapped onto 0-100 by a saturating curve. The formula, the
  per-factor breakdown, the raw rate, and the caveat all travel in the same payload and render in the
  same card as the number, because a score that can be screenshotted away from its definition will be
  ([ADR 018](docs/adr/018-geopolitical-activity-score.md)).
- **Analytics aggregate in SQL; only what SQL cannot express is sampled.** Every breakdown is a
  `GROUP BY` returning one row per class, so its cost tracks the number of classes rather than how
  busy the period was. The timeseries and the score need per-incident time arithmetic over timestamps
  stored as converted ticks, which SQLite cannot bucket or decay, so those two share one capped
  four-column projection — and the response says when the cap bound
  ([ADR 019](docs/adr/019-analytics-aggregation.md)).
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
| `GET /api/spatial/incidents-near` | Incidents within a radius of a point, nearest first, with measured distances |
| `GET /api/spatial/chokepoints` | Recorded activity around each watched maritime chokepoint |
| `GET /api/analytics` | Distributions, timeseries, maritime summary, and activity score for one window (`?window=24h\|7d\|30d\|90d`) |
| `GET /api/analytics/windows` | The windows analytics can be requested over |
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
| `Pipeline:MinimumCorrelationConfidence` | 0.45 | Turn up when incidents merge that should not |
| `Pipeline:SemanticSimilarityThreshold` | 0.4 | How much wording must agree to count as corroboration |
| `Pipeline:PlaceNameConfidence` | 0.55 | Ceiling when co-location rests on a shared place name |
| `Pipeline:ContentOnlyConfidence` | 0.6 | Ceiling when neither report can be placed at all |
| `Pipeline:SupportFloor` | 0.6 | How far weak corroboration may pull a match below its ceiling |
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
| `Providers:Mode` | Demo | `Demo` makes no external call; `Live` allows individually enabled adapters to poll |
| `Providers:Rss:Enabled` | false | Whether configured feeds are polled |
| `Providers:Rss:Feeds` | empty | `Name` and `Url` per feed; no publisher is baked into the code |
| `Providers:NasaFirms:Enabled` | false | Whether thermal detections are polled |
| `Providers:NasaFirms:ApiKey` | none | **Never put this in a file.** Dormant without it. |
| `Providers:NasaFirms:Dataset` | VIIRS_NOAA20_NRT | FIRMS dataset identifier |
| `Providers:NasaFirms:Area` | world | `west,south,east,north`, or `world` |
| `Providers:NasaFirms:MinimumConfidence` | 50 | Detections below this are noise and are dropped |
| `Providers:Acled:Enabled` | false | Whether coded conflict events are polled |
| `Providers:Acled:ApiKey` / `:Email` | none | **Never put these in a file.** Dormant without both. |
| `Providers:*:PollInterval` | 15 min / 1 h / 6 h | Per-provider polling cadence |
| `Providers:*:MaxItemsPerPoll` | 25 / 50 / 50 | Ceiling on envelopes emitted from one poll |
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

216 tests cover domain invariants, fingerprinting, classification, correlation scoring, queue
backpressure and cancellation, gazetteer resolution, and the processor's failure paths; the AI trust
boundary (malformed JSON, unknown enums, out-of-range confidence, oversized payloads, control
characters, prompt-injection fixtures, provider timeout, provider exception, repair success and
exhaustion); the OSINT adapters against recorded provider payloads (RSS 2.0, Atom, VIIRS and MODIS
CSV, ACLED JSON, plus malformed bodies, an external-entity declaration, a rate limit, a server
outage, and a rejected credential) driven through the application's real HTTP and resilience stack;
the correlation signals (lexical similarity, entity overlap, the positional ceiling, and the
over-merge guards) and the per-category gate; the spatial layer (bounding-box containment around a
full circle of bearings, antimeridian wrap, pole spanning, the corner case the rectangle admits and
the circle rejects, and chokepoint counting and ordering); the analytics layer (score bounds, monotonicity in each of
the four factors, the corroboration ceiling, rate-invariance across windows, half-open window
boundaries, and full-length timeseries including empty buckets); end-to-end integration tests that
drive the real host
and assert on what the API then serves, including a concurrency test that reproduces the correlation
race and is verified to fail when the gate is removed; and the evaluation harness above.

## Not yet implemented

There is no ML severity model yet, so the LLM-versus-ML-versus-label comparison has nothing to
compare. It is the remainder of Sprint 5 and is deliberately not represented as working before then.

Four limitations worth stating plainly:

- **The activity score summarises this database, not the world.** It measures what the system
  ingested. A quiet score may mean a quiet period, or it may mean no adapter was configured and
  nothing arrived — the score cannot tell those apart. Its saturation constant is calibrated against
  the volumes this project produces, so scores from two differently-calibrated deployments are not
  comparable ([ADR 018](docs/adr/018-geopolitical-activity-score.md)).

- **The globe has no 3D terrain or buildings.** Those need a Cesium ion token, which is a credential,
  so they stay out. Imagery is 2D draped on a sphere.

- **The default AI provider is a deterministic stand-in, not a language model.** Everything it
  produces is labelled as such. The published snapshot was built with it.
- **The live OSINT adapters have never polled the real services.** They are tested against recorded
  payloads only, for the reasons given above. The published dashboard is built from the recorded
  replay stream, so nothing on it came from a live provider.
- **Correlation cannot corroborate across categories.** Candidates are pre-filtered by event type,
  so a satellite thermal detection is never linked to a piracy report however close it is. That is a
  deliberate trade, and it means cross-source corroboration works between sources that agree on a
  category rather than between sources describing one event in different terms.
- **Similarity is shared vocabulary, not meaning.** It cannot recognise a paraphrase with no words in
  common, or one event reported in two languages.

  See [docs/architecture.md](docs/architecture.md#known-limitations) for the full list.

See the [ADRs](docs/adr/) and the authoritative [project plan](GeoConflux_Plan.md).
