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

Sprints 1 to 9 are complete, along with the final architecture review that follows them, and
Sprint 11 — the two global conflict datasets read as the archives they are, with bounded and
resumable history. What remains is in [the global coverage assessment](docs/global-coverage-plan.md).

Three reviews are written up rather than summarised. The
[security review](docs/security-review.md) found and fixed a credential that was being written to the
logs on every poll, a redirect path that could have sent this process into a private network, and an
open write path that could hold requests open indefinitely. The
[dependency review](docs/dependency-review.md) covers all 465 resolved packages, the two CDN assets
outside NuGet's reach, and the four GitHub Actions.

The [architecture review](docs/architecture-review.md) is the hostile pass over the remaining
eighteen dimensions. It found seven defects, no Critical ones, and fixed all seven. The three ranked
High were a concurrent redelivery being reported as a pipeline failure and its evidence discarded, a
failure-recovery path that re-committed the very unit of work it was recovering from, and a
supplementary severity model that could fail the ingestion of real reporting — each one a case where
a comment in the code asserted a guarantee the code did not keep. Every finding is demonstrated by a
test that fails without the fix; the review also records what was examined and deliberately left
alone, including an N+1 that was measured and judged not worth removing.

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
| ACLED | `acled` | `Providers:Acled:Username` + `:Password` | coordinates and their stated precision, category, fatality-derived severity |
| UCDP GED | `ucdp` | `Providers:Ucdp:AccessToken` | coordinates and their stated precision, category, death-derived severity |

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

## Agent-collected OSINT

Polling only finds what a publisher chose to broadcast. A feed cannot be tasked at a region or a
topic, never reads past the headline, and decides what is visible by what happened to fall inside its
window. So there is a second intake shape alongside it: an OSINT agent works against a committed
brief, reads the documents, and records a **collection bundle** — a JSON file of cited items that the
application reads from disk.

That is what lets the published page show real reporting. A bundle needs no credential, opens no
connection, is reviewable in a diff, and reads the same on every run, so the deploy can publish cited
material rather than a recorded demo.

**The collector may state a citation and nothing resembling a conclusion.** It reports who published
what, when, where to read it, a bounded verbatim excerpt, and place names appearing in that text. It
may not report a coordinate, an event type, a severity, a paraphrase in place of a quotation, or
anything it did not retrieve. That is not enforced by asking nicely: the bundle schema has *no field*
for a coordinate, a category, or a severity, and a test asserts their absence so adding one fails
with a message saying why it must not exist.

A bundle is untrusted input and gets no credit for who wrote it. It is validated by the same kind of
pure parser the provider adapters use, pinned to recorded fixtures covering the malformed cases:
unknown schema version, unmapped property, future dates, an excerpt over the cap, a cited URL
pointing into private address space, a disallowed platform, a duplicate URL, a bad hash. A structural
fault rejects the file; a bad item is skipped with a stated reason and the rest survives.

Records now carry one of three provenances rather than a demo/live boolean, because a bundle is real
reporting gathered at a stated moment — calling it demo data would be a lie in one direction and
calling it a live feed a lie in the other:

| Provenance | Meaning | Shown as |
| --- | --- | --- |
| `Recorded` | The synthetic replay stream | `DEMO` |
| `Polled` | An adapter reached its provider during this run | no chip |
| `Collected` | An agent gathered it into a bundle | `COLLECTED`, with the collection date |

The bundle committed here was collected on 2026-09-12 against
[`data/osint/briefs/maritime-chokepoints.md`](data/osint/briefs/maritime-chokepoints.md): seven items
from UN News in English and Arabic and from the Times of Israel. Two of them are the same story in
one publisher's two language editions, kept separate on purpose — the divergence between editions is
itself the observation.

Three of the seven are Arabic, and all three are placed on the globe, which is only true because the
gazetteer gained native-script aliases at the same time. Collecting in twenty languages while
resolving in one produces a dashboard that sees more of the world and plots less of it.

**What is not read.** X answers an unauthenticated request with HTTP 402, and its `robots.txt`
separately disallows the profile path; Weibo redirects to an authentication wall (all checked,
2026-09-13). Neither is scraped around, and no account is created to present an automated collector
as a person — the gate exists to prevent exactly that. They are recorded as gaps, because an
unrecorded gap reads as coverage the system does not have. A paid credential would move such a
source into the ordinary authenticated-adapter pattern ACLED uses.

**What is read, and how carefully.** Telegram public channel previews, Bluesky author feeds and
Mastodon public timelines are all publicly readable with no credential, and are collected by
`tools/collect`. The access rules are code rather than a paragraph in a brief: robots.txt is
honoured, every request identifies itself and waits its turn, and a gate is a refusal that is never
retried. Two rules look identical at the call site and mean opposite things — a *missing* robots.txt
permits everything and an *unreachable* one forbids everything — so both are asserted, along with
forty-three others, in an offline self-test that CI runs on every push.

Full reasoning: [ADR 025](docs/adr/025-agent-collected-osint.md) and
[ADR 030](docs/adr/030-collection-access-policy.md).

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
| Location name — precision / recall / F1 | 1.00 / 0.92 / 0.96 |
| Entities — precision / recall / F1 | 0.11 / 0.67 / 0.19 |

The entity figure is poor because the stand-in finds capitalised runs, which recovers most of the
right names and a lot of noise. It is reported rather than tuned away: it is the gap a real model is
expected to close.

The location figures have a history worth keeping. Precision fell from 1.00 to 0.82 when the
gazetteer grew from 66 entries to 214, and that was recorded here as the honest cost of a wider net —
more names to match meaning more names matched wrongly. **That explanation was wrong.** The real
cause was that place names were matched as bare substrings, so the two-letter alias `US` hit inside
"because", "thus", and "Russia", and because the search took the earliest match in the text, one such
hit outranked the real place name later in the sentence. Every extra alias made a genuine bug look
more like a reasonable trade-off.

Matching is now boundary-aware, and script-aware about what a boundary is, since Han, Kana, Hangul
and Thai write without word breaks. Precision returned to 1.00 — the false positives are gone
entirely, which is what that number means. Recall rose from 0.75 to 0.92 separately, because the
gazetteer gained native-script aliases and the baseline can now read the Arabic, Russian, and Chinese
fixtures it previously could not.

The episode is left in rather than tidied away, because the failure it illustrates is the expensive
kind: a plausible explanation attached to a real regression, which stops the next person looking.

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
| `Providers:NasaFirms:MinimumRadiativePowerMegawatts` | 0 | Fire radiative power floor; 0 accepts any |
| `Providers:NasaFirms:NightOnly` | false | Accept only night-side detections. Agricultural burning is a daytime activity. |
| `Providers:NasaFirms:PersistentSourceDays` | 3 | Days a location must burn on to be treated as infrastructure. Needs `DayRange` above 1 to see anything. |
| `Providers:Acled:Enabled` | false | Whether coded conflict events are polled |
| `Providers:Acled:Username` / `:Password` | none | **Never put these in a file.** Dormant without both. |
| `Providers:Acled:BaseAddress` | `https://acleddata.com/api/` | Current API root. The retired `api.acleddata.com` no longer resolves. |
| `Providers:Acled:TokenEndpoint` | `https://acleddata.com/oauth/token` | Where the account credential is exchanged for a token |
| `Providers:Acled:Countries` | empty | Country names to request, as ACLED spells them. Empty means no filter. |
| `Providers:Ucdp:Enabled` | false | Whether UCDP georeferenced events are polled |
| `Providers:Ucdp:AccessToken` | none | **Never put this in a file.** Requested from UCDP by email. Dormant without it. |
| `Providers:Ucdp:Resource` / `:Version` | `gedevents` / `26.0.7` | GED Candidate, the monthly series |
| `Providers:Ucdp:Countries` | empty | Gleditsch and Ward numbers, not ISO codes. Empty means no filter. |
| `Providers:{Acled,Ucdp}:MaxItemsPerPoll` | 50 | Rows per **request**, not per poll. These two are datasets, so a poll can make several. |
| `Providers:{Acled,Ucdp}:MaxRequestsPerPoll` | 4 | The bound on one poll, shared by the live window and the backfill |
| `Providers:{Acled,Ucdp}:BackfillSince` | none | How far back to walk history. Absent means no backfill at all, which is the default. |
| `Providers:{Acled,Ucdp}:BackfillWindow` | 7 days | How much history each step of the walk requests |

### Datasets are archives, not feeds

ACLED and UCDP have years of coded conflict behind them, and an archive can answer a question
*partially* without saying so — a response holding as many rows as you asked for looks exactly like a
complete one. So both adapters ask for a bounded window of dates, and narrow it when the provider
signals it sent less than it holds. UCDP states how many pages a query matched; ACLED publishes no
such flag, so a full page is treated as possibly cut off. A window still truncated at the one-day
floor is logged as a real gap rather than passed over.

History is walked backwards under a per-poll request budget and resumes from a record of how far back
each source has already asked — stored rather than derived from the oldest record held, because a
quiet fortnight returns nothing and a walk driven from the data would re-request it forever. A window
left half-read never advances that record.

The published dashboard gets the live window only. Its export runs against a throwaway database, so a
resumable walk has nothing to resume from; backfill belongs to a deployment that keeps its data.
[ADR 031](docs/adr/031-dataset-history.md) records the reasoning and
[the operator notes](docs/operations/dataset-credentials.md) cover turning either one on.

### A post is not a report

A wire item comes from an organisation with an editorial process, a correction policy, and a
reputation it is unwilling to spend. A Telegram post comes from a handle. It may be the best account
of an event that day, or an anonymous claim, or footage recycled from a different war, and nothing
about the item alone distinguishes those.

So a user-generated claim that matches no incident is **stored, classified, placed, and drawn on the
map — with no incident.** What is withheld is not the record but the assertion: an incident is this
system saying something happened, an observation is it saying a source said this, and one post
supports only the second. The claim is released the moment a second independent source arrives,
whether that is published reporting or another channel, because social breaks first and the wire
follows.

Holding rather than hiding is the load-bearing half. A hidden claim is a claim nobody can
corroborate, and hiding them would also conceal how much of the picture rests on unsupported posts.
The dashboard therefore says *Reported by* a publisher or *Claimed on* a channel, and chips a held
claim distinctly from a corroborated one — a reader must never have to guess which they are looking
at.

The same rule governs `POST /api/observations`. It takes no credential, so a submission is the
definitional user-generated claim: it can join an incident that exists on other grounds and can
never bring one into being, because two anonymous strangers agreeing is one unverifiable assertion
repeated.

Full reasoning: [ADR 029](docs/adr/029-corroboration-gate.md).

### Coverage

The dashboard has a **Coverage** tab that states, per theatre, how much has been placed and how
precisely, which sources it came from, how many place names the lexicon holds, and what that
theatre's numbers cannot tell you. Beside it are counts by country, language, source tier and
platform, and the last collection run's account of what each source it asked actually gave.

It exists because a map is silent about its own gaps. Three dots over Tigray look identical whether
three things happened or three things were reported, and the tab makes that difference explicit —
including saying plainly that Tigray coverage is sparser than its conflict, and why.

The source list is the half that counting cannot supply. A channel that refused, a channel that
publishes nothing, a channel read that had nothing relevant to say, and a channel the diversity caps
emptied are four different statements that otherwise reduce to one absence — and an absence on a map
reads as *nothing happened there* rather than as *we did not see*. The most recent run records all
four, plus the countries absent from the table entirely, which were not looked at rather than quiet.

### Place names

The gazetteer has three layers, and the code says which is which because they have different
provenance and different rules.

| Layer | What it holds | Source | Size |
| --- | --- | --- | --- |
| Curated core | Chokepoints, seas, country centroids, and the alias judgements that make ordinary reporting language resolve | Hand-written in `Gazetteer.cs` | ~250 entries |
| Global coarse | Every first- and second-order administrative unit on earth, and the town that is the seat of each | GeoNames, CC BY 4.0 | 78,547 places in 246 countries, 193,445 spellings |
| Theatre deep | Settlements below district level, for theatres under active tasking | Wikidata, CC0 | 2,750 places, 9,179 spellings |

Coordinates are sourced rather than typed. That is the point of the arrangement: a latitude written
from recollection is indistinguishable in the file from a correct one, which is the same failure
[ADR 005](docs/adr/005-location-resolution.md) refuses when a model proposes a coordinate.

A place is held under the names its own country uses for it plus the ones the wire uses, because a
conflict is reported in the language it happens in. Finding a name in prose uses an Aho-Corasick
automaton rather than a scan per spelling: one pass over the text, 0.024 ms, and a cost that does not
grow with the lexicon.

The coarse layer answers when something *names* a place — a coded dataset, an enrichment provider, a
submission. It is deliberately not hunted for in running prose, because holding every administrative
unit on earth means holding thousands named after ordinary words, and *Along*, *Maritime*, *Centre*,
*Police* and *Exchange* are all real places.
[ADR 033](docs/adr/033-tiered-gazetteer-artefact.md) has the measurement.

To refresh the extracts, run `python tools/gazetteer/global.py` and `python tools/gazetteer/extract.py`.
The build never fetches either. [ADR 032](docs/adr/032-global-gazetteer-sourcing.md) has the licence
comparison, [ADR 026](docs/adr/026-gazetteer-sourcing.md) the collision rules, and `NOTICE` the
attribution GeoNames requires.
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
npm test                                    # the dashboard client; needs Node, installs nothing
dotnet format GeopoliticsDashboard.sln --verify-no-changes
docker build -t geopolitics-dashboard .
```

312 .NET tests cover domain invariants, fingerprinting, classification, correlation scoring, queue
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

A further **81 tests cover the dashboard client**, which is JavaScript and so is a separate suite and
a separate command. They cover the escaping that stops a hostile feed title becoming markup, the
fallback from a live backend to the exported snapshot, the time basis that keeps a replayed record
from reading as though it happened today, the fail-safe rule that decides whether the demo-data
notice may come down, incident filtering and ordering, and the confidence and model-opinion chips.
The runner is `node:test`, so the suite installs nothing and the repository carries no JavaScript
dependencies; [ADR 024](docs/adr/024-dashboard-test-runner.md) records that decision and states what
is still verified by loading the page rather than by a test.

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
- **NASA FIRMS, ACLED and UCDP have never polled the real services.** All three need a credential,
  so they are tested against recorded payloads only. Their request shapes are built against each
  provider's published documentation and pinned by fixtures; that is not the same as having been
  answered by the live service, and it is not claimed to be. The RSS adapter is no longer in that
  position: the published dashboard is built by polling four public feeds on every deploy, so what
  you see there did come from live providers. Anything still untested is described as untested rather
  than as working.
- **Coverage is global in capability, not yet in fact.** The two datasets that would make it global
  are built and dormant, waiting on credentials nobody has requested. Placement is no longer the
  blocker it was — the lexicon spans 246 countries — but it is coarse outside the three deep
  theatres: districts and district towns, not villages, and the Coverage tab states the ceiling per
  country. Fifty-two countries are held by fewer than twenty names each. See
  [the global coverage assessment](docs/global-coverage-plan.md) for what remains and in what
  order.
- **Correlation cannot corroborate across categories.** Candidates are pre-filtered by event type,
  so a satellite thermal detection is never linked to a piracy report however close it is. That is a
  deliberate trade, and it means cross-source corroboration works between sources that agree on a
  category rather than between sources describing one event in different terms.
- **Similarity is shared vocabulary, not meaning.** It cannot recognise a paraphrase with no words in
  common, or one event reported in two languages.

  See [docs/architecture.md](docs/architecture.md#known-limitations) for the full list.

See the [ADRs](docs/adr/) and the authoritative [project plan](GeoConflux_Plan.md).
