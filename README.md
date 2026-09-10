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

Sprints 1 and 2 are complete. The application ingests a recorded observation stream, processes it
asynchronously, and streams results to the dashboard in realtime.

```text
IEventSource -> validation -> bounded Channel -> background processor
             -> normalise -> deduplicate -> resolve location
             -> correlate -> persist -> SignalR -> dashboard
```

Concretely, running the app locally will:

- ingest eight recorded observations from four synthetic sources, with realistic arrival delays;
- reject one byte-identical redelivery as a duplicate, while keeping it for audit;
- correlate two differently-worded reports of the same event into a single incident;
- place observations using provider coordinates or a local gazetteer, and leave one deliberately
  unmappable report visible without coordinates;
- push each result to the browser over SignalR with no page refresh.

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
- **No AI or ML is used yet.** Categories and severities come from a deterministic keyword
  classifier ([ADR 011](docs/adr/011-deterministic-classification-before-ai.md)). It computes a
  confidence score that is deliberately capped well below certainty, because keyword matching does
  not deserve more. That score is not yet surfaced in the UI — threading it through the response
  contracts is the next increment, and until then the dashboard shows the label without it.

See [docs/architecture.md](docs/architecture.md) for the stage ordering and failure behaviour.

## Run locally

Install .NET SDK 10.0.401 or a compatible .NET 10 feature band, then run:

```powershell
dotnet restore GeopoliticsDashboard.sln
dotnet run --project src/Geopolitics.Api
```

Open the URL printed by ASP.NET. The replay stream begins immediately; watch the **Live feed** tab to
see observations arrive and the globe update without a refresh.

No credentials of any kind are required. Live providers will be opt-in through configuration in a
later sprint, and credentials will stay outside source control.

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
dotnet run --project src/Geopolitics.Workers --configuration Release --no-build -- --export "$PWD/dist/data"
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

The suite covers domain invariants, fingerprinting, classification, correlation scoring, queue
backpressure and cancellation, gazetteer resolution, and the processor's failure paths, plus
end-to-end integration tests that drive the real host and assert on what the API then serves.

## Not yet implemented

There is no AI enrichment, no live external feed, no spatial querying, no analytics, and no ML model
yet. Those arrive in Sprints 3 to 6 and are deliberately not represented as working before then.

The current classifier is keyword-based, and its confidence scores reflect that rather than
pretending to be model output.

See the [ADRs](docs/adr/) and the authoritative [project plan](GeoConflux_Plan.md).
