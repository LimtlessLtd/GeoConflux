# GeoConflux

GeoConflux is a .NET 10 modular-monolith geopolitical intelligence platform. It distinguishes source
observations from the correlated incidents shown to users, and is designed to demonstrate a
trustworthy asynchronous processing pipeline rather than just a map.

**Live dashboard: [limtlessltd.github.io/GeoConflux](https://limtlessltd.github.io/GeoConflux/)**

> The dashboard runs on **synthetic replay data**. It is labelled as demo data in the API, in the
> exported snapshot, and in the UI. It is not live reporting and describes no real-world events.

## Live data

The published dashboard is built by polling four public feeds — UN News, ReliefWeb, BBC World, and
Al Jazeera — on every deploy and on a weekly schedule. No credential is involved; the feeds are
public and are read as RSS is meant to be read. A recent run ingested 95 real reports, placed 86% of
them, and correlated several across outlets.

Real headlines are shown with this system's own assessments beside them, and the two are never
conflated: categories, severities, and correlations are GeoConflux's, not the publishers'. The
recorded demo stream is still ingested alongside, so the page cannot go blank if a feed is
unreachable, and every record is labelled individually as live or demo — the banner counts them
rather than asserting a blanket label.

That labelling reaches the timestamps too. A live report is aged against the reader's own clock,
because a real publisher really did put it out four hours ago and that stays true however long after
the run the page is opened. A demo record is aged against the recorded run instead, because its
timestamp is invented and measuring it against now would stamp a fabricated event on a real strait as
though it had happened this afternoon. So the list shows "4hr ago" beside "35m before the run", and
the difference is the point rather than an inconsistency.

What real data makes obvious, and the page does not hide: the default enrichment provider is a
deterministic keyword stand-in rather than a language model, and it cannot categorise most real
reporting. Roughly three quarters of live incidents land in `Other`. That is the honest state of the
offline baseline, and it is the gap a configured provider is expected to close.

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

Sprints 1 to 6 are complete. Sprint 6's last outstanding items — a formal dependency review and a
security review — are done and written up in [docs/dependency-review.md](docs/dependency-review.md)
and [docs/security-review.md](docs/security-review.md). The security review found and fixed a
credential that was being written to the logs on every poll, a redirect path that could have sent this
process into a private network, and an open write path that could hold requests open indefinitely.

The application ingests a recorded observation stream, enriches each item through a schema-validated
AI stage, processes it asynchronously, scores it with a trained severity model, streams results to
the dashboard in realtime, and summarises what it has stored across four time windows. Adapters for RSS, NASA FIRMS, and ACLED feed the same pipeline when they
are configured; all three ship disabled.

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
  incidents over time, and a documented activity score shown with the formula that produced it;
- score every accepted observation with a trained severity model and record what it would have said
  beside what the pipeline actually applied, so the disagreements are visible in the evidence list.

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
| Location name — precision / recall / F1 | 0.82 / 0.75 / 0.78 |
| Entities — precision / recall / F1 | 0.11 / 0.67 / 0.19 |

The entity figure is poor because the stand-in finds capitalised runs, which recovers most of the
right names and a lot of noise. Location recall is 0.75 because the baseline cannot read the Arabic,
Russian, and Chinese fixtures. Both are reported rather than tuned away: they are the gap a real
model is expected to close.

Location precision fell from 1.00 to 0.82 when the gazetteer grew from 66 entries to 214 to cover
real reporting. With more names to match, the stand-in now sometimes names a place the fixture did
not label — a wider net catching more, including more of what was not asked for. That is the trade
that made 86% of live reports placeable, and it is recorded here rather than smoothed over, because a
table of metrics that only ever improves is a table nobody is really reading.

Sixteen synthetic, author-labelled cases cannot support a claim about geopolitical classification
ability. The set exists to catch regressions. Full method, per-class tables, and limitations:
[tests/data/ai-evaluation/](tests/data/ai-evaluation/) and the generated
[RESULTS.md](tests/data/ai-evaluation/RESULTS.md).

To measure a real model instead, set `GEOCONFLUX_EVAL_PROVIDER` and `GEOCONFLUX_EVAL_MODEL` and
rerun `dotnet test tests/Geopolitics.AiEvaluationTests`.

## The severity model, and what it is measured against

A conventional multiclass model — ML.NET, SDCA maximum entropy — is trained in-process from a corpus
of 140 synthetic, author-labelled reports and scores every observation the pipeline accepts. It sees
the report text, the assigned category, and four features a bag of words cannot: how many sources
support it, how confident the classification was, how many actors were named, and whether it could be
placed.

It is scored on 45 held-out cases it was never trained on, and the enrichment path is run over the
**same** cases through the **same** provider configuration, so the two are comparable rather than
merely both present.

| Measure | Severity model | Enrichment provider |
| --- | ---: | ---: |
| Accuracy against labels | 0.56 | 0.38 |
| Macro F1 against labels | 0.51 | 0.32 |
| Correct of 45 | 25 | 17 |

The two agreed with each other on 18 of 45 cases. Agreement is reported but is never presented as
accuracy — both can agree and both be wrong — which is why both are also scored against the labels.

The single claim this makes for the model is the margin between those accuracy figures, and that
margin is **asserted**: if the model stops beating the deterministic classifier already in the
pipeline, the build fails. At that point the honest response is to remove it, and the test makes that
visible rather than leaving a model in place because it is there.

Three things about the harness are worth knowing, because they are what makes the numbers mean
anything:

- **The split is written into the corpus**, not drawn at evaluation time. A split re-randomised per
  run would make every figure a different measurement and a regression indistinguishable from a
  reshuffle.
- **A leak is asserted against by id and by text.** It would raise every figure in the report while
  leaving it looking entirely reasonable.
- **Training twice produces the same model**, also asserted. Without that the figures would be a
  sample rather than a measurement.

The model is not good, and that is reported rather than tuned away: its recall on `CRITICAL` is 0.25
and on `HIGH` is 0.30, because with 95 training rows a regularised linear model hedges toward the
middle classes. Tuning it against a 45-case holdout would be fitting the holdout.

Full per-class tables, every case where the two disagreed, the labelling rubric, and the limitations:
[tests/data/severity-model/RESULTS.md](tests/data/severity-model/RESULTS.md) and
[the corpus README](src/Geopolitics.Infrastructure/Ml/Data/README.md).

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
- **A trace says which stage was slow, not just that something was.** Every processed item opens a
  span per stage — deduplicate, enrich, score severity, resolve location, correlate, persist,
  publish — tagged with the decision that stage reached, and each emits a duration metric under the
  same stage name from the same call. Spans and metrics kept in separate call sites are how a trace
  and a dashboard come to describe one pipeline in two vocabularies and then disagree about which
  part is slow. That the telemetry is emitted at all is asserted by tests, because a span that never
  starts looks exactly like a working system until the incident it existed for.
- **Per-item cost must not grow with what is already stored.** A throughput test drives 400
  observations through the real pipeline and the real database and compares the median cost of the
  first half against the second. That comparison, not the absolute number, is the assertion: a
  correlator that rescores every incident ever recorded, or a lookup that quietly became a table
  scan, shows up as a second half that costs more than the first. Measured on the development
  machine: 400 observations in 2.1s, median 3.3 ms per item in both halves.
- **An external integration fails in its own blast radius.** Timeout, exponential backoff with
  jitter, `Retry-After`, and a circuit breaker come from the standard .NET resilience handler; a
  4xx that means "your request is wrong" is not retried, so a bad credential fails once rather than
  four times. A provider that is down, slow, rate-limited, or returning nonsense costs its own poll
  and nothing else ([ADR 015](docs/adr/015-live-provider-ingestion.md)).
- **Evidence survives failure.** A failed enrichment, geocode, or correlation retains the source
  payload with a recorded reason instead of discarding it.
- **What a poll may reach is decided at the socket, not at the URL.** A feed URL is a deployment
  decision and can be trusted; the response cannot. Redirects are screened against the address a name
  actually resolved to, so loopback, link-local, and private space are refused whether they are
  reached directly, through a redirect chain, or through a hostname that resolves there. Checking the
  hostname instead would catch only the naive attempt
  ([ADR 021](docs/adr/021-outbound-trust-boundary.md)).
- **A credential is never written down, and that is asserted rather than commented.** One upstream
  API takes its key as a URL path segment, which the runtime's default HTTP logging writes verbatim.
  Outbound logging for the provider clients is replaced with one that masks configured credentials
  and drops query values. The tests capture every line the container emits and assert the credential
  was sent but does not appear, so they prove redaction rather than absence of a request
  ([ADR 021](docs/adr/021-outbound-trust-boundary.md)).
- **Running without accounts is paid for, not assumed free.** The bounded queue makes producers wait
  when it is full, which is right for a polling adapter and wrong for an HTTP caller whose wait costs
  a held connection. The submission endpoint bounds its wait and answers `503`, is rate limited per
  client with `Retry-After`, and the request body cap is two orders of magnitude below the framework
  default ([ADR 022](docs/adr/022-open-write-path-and-content-policy.md)).
- **The dashboard's escaping has something behind it.** Every render path escapes untrusted feed text,
  and that is what prevents injection; a content security policy is what limits the damage if one of
  those paths is ever written wrongly. It is declared in the page rather than as a header, because the
  published dashboard is served by GitHub Pages, which sets no headers — a policy in the API alone
  would protect the local page and leave the public one bare. It is verified by loading the page twice
  in a real browser, with the policy and without, and comparing the failed requests — because a worker
  blocked by a policy logs no violation at all, which is how the first draft of it quietly broke two
  of Cesium's ([ADR 022](docs/adr/022-open-write-path-and-content-policy.md)).
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
- **The severity model is a second opinion, structurally — not by convention.** A multiclass
  logistic-regression model (ML.NET, SDCA maximum entropy) scores every accepted observation, and the
  domain gives it no way to change a stored severity: it writes to `ModelSeverity`, a missing
  prediction is `null` rather than a default, and every failure in the stage is swallowed. A second
  opinion that can fail an observation is a new dependency, not an addition
  ([ADR 020](docs/adr/020-severity-model-as-second-opinion.md)).
- **The corpus is the artefact; the model is derived from it.** The model is trained in-process from
  a labelled corpus embedded in the assembly rather than loaded from a committed `.zip`. A committed
  binary is an artefact nobody can diff or verify came from the dataset beside it, and the version it
  claims is whatever the last person to regenerate it typed. Training from the corpus makes "this
  model matches this dataset" true by construction, and a fixed seed with a single training thread
  makes two runs produce the same model — asserted by a test, because otherwise the evaluation
  figures would be a sample rather than a measurement.

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
| `GET /api/severity/model` | Whether the severity model is available, and which model it is |
| `POST /api/severity/predict` | Scores a report with the trained model, returning every class probability |
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
| `SeverityModel:Enabled` | true | Whether the trained severity model runs. Off removes the second opinion and nothing else. |
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

272 tests cover domain invariants, fingerprinting, classification, correlation scoring, queue
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
boundaries, and full-length timeseries including empty buckets); the severity model (holdout
isolation by id and by text, class coverage in both splits, training reproducibility, and the four
negative cases that keep it a second opinion — no override, null rather than a default, a throwing
model absorbed, and a disabled one silent); end-to-end integration tests that drive the real host
and assert on what the API then serves, including a concurrency test that reproduces the correlation
race and is verified to fail when the gate is removed; the telemetry itself (a span per stage, all
under one trace, carrying the decision each stage made, and the matching stage-duration and model
counters); a throughput run of 400 observations through the real database that asserts per-item cost
does not grow as the table fills; a cancellation run that asserts processing stops promptly and
leaves nothing half-committed; and the evaluation harnesses above.

## Not yet implemented

Five limitations worth stating plainly:

- **The severity model learned one author's rubric, not geopolitics.** It is trained on 140
  synthetic, author-labelled reports and scores 0.56 accuracy on 45 held-out cases against the
  enrichment baseline's 0.38. That is a real supervised-learning result and it is the only claim made
  for it. Its recall on `CRITICAL` is 0.25 — with 95 training rows a regularised linear model hedges
  toward the middle classes — which is reported rather than tuned away, because tuning it on a
  45-case holdout would be fitting the holdout. Full figures and every disagreement are in
  [tests/data/severity-model/RESULTS.md](tests/data/severity-model/RESULTS.md).
- **The activity score summarises this database, not the world.** It measures what the system
  ingested. A quiet score may mean a quiet period, or it may mean no adapter was configured and
  nothing arrived — the score cannot tell those apart. Its saturation constant is calibrated against
  the volumes this project produces, so scores from two differently-calibrated deployments are not
  comparable ([ADR 018](docs/adr/018-geopolitical-activity-score.md)).

- **The globe has no 3D terrain or buildings.** Those need a Cesium ion token, which is a credential,
  so they stay out. Imagery is 2D draped on a sphere.

- **The default AI provider is a deterministic stand-in, not a language model.** Everything it
  produces is labelled as such. The published snapshot was built with it.
- **NASA FIRMS and ACLED have never polled the real services.** Both need a credential, so they are
  tested against recorded payloads only. The RSS adapter is no longer in that position: the published
  dashboard is built by polling four public feeds on every deploy, so what you see there did come
  from live providers. Anything still untested is described as untested rather than as working.
- **Correlation cannot corroborate across categories.** Candidates are pre-filtered by event type,
  so a satellite thermal detection is never linked to a piracy report however close it is. That is a
  deliberate trade, and it means cross-source corroboration works between sources that agree on a
  category rather than between sources describing one event in different terms.
- **Similarity is shared vocabulary, not meaning.** It cannot recognise a paraphrase with no words in
  common, or one event reported in two languages.

  See [docs/architecture.md](docs/architecture.md#known-limitations) for the full list.

See the [ADRs](docs/adr/) and the authoritative [project plan](GeoConflux_Plan.md).
