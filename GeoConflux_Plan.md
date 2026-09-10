# Geopolitics Dashboard — AI Coding Agent Master Plan

> **Status:** Authoritative project specification.
>
> **Primary objective:** Build a portfolio-quality geopolitical intelligence platform that demonstrates senior-level .NET engineering, architecture, AI/ML engineering, data engineering, testing, observability, resilience, and production-readiness.
>
> **Important:** This is not merely a map application. The map is the visualisation layer for a robust asynchronous event-processing platform.

---

## 1. Instructions to the Coding Agent

You are the primary implementation agent for this repository.

You are expected to inspect the repository, implement the project incrementally, run builds/tests, diagnose failures, and continue through all planned sprints without requiring the human developer to provide implementation details for each individual feature.

### Primary instruction

**Treat this document as the authoritative project specification unless the repository contains a newer, explicit architectural decision recorded in `docs/adr/`.**

When an existing ADR conflicts with this document, prefer the ADR and explain the conflict before making significant architectural changes.

### You must

- Inspect existing code before making changes.
- Preserve working functionality.
- Build incrementally rather than rewriting large sections unnecessarily.
- Respect project boundaries and dependency direction.
- Add tests alongside features.
- Run `dotnet build` and `dotnet test` after meaningful changes.
- Fix build/test failures rather than bypassing them.
- Use real implementations where practical and deterministic mocks/fixtures where external services are unavailable.
- Keep the project runnable without external API credentials.
- Keep a deterministic replay/demo mode available.
- Record meaningful architectural decisions in ADRs.
- Update documentation as the implementation evolves.
- Never fabricate live data, AI evaluation results, ML metrics, performance figures, or test results.

### You must not

- Introduce microservices merely to appear more enterprise.
- Introduce Kafka, RabbitMQ, Redis, Kubernetes, etc. without a demonstrated need.
- Couple the processing pipeline to SignalR.
- Allow an LLM to be the authoritative source of latitude/longitude.
- Put infrastructure concerns directly into the Domain layer.
- Disable tests to make the build pass.
- Remove error handling to simplify implementation.
- Hard-code secrets/API keys.
- Commit external credentials.
- Store unrestricted chain-of-thought/reasoning traces from an LLM.
- Pretend an external API was called successfully when it was not.
- Invent AI/ML metrics.

### Agent workflow

For each sprint:

1. Inspect the current repository.
2. Summarise the current architecture and identify the next coherent increment.
3. Implement the sprint requirements.
4. Add/modify tests.
5. Run formatting/build/tests.
6. Fix genuine problems found by verification.
7. Update documentation/ADRs.
8. Provide a concise implementation summary.
9. Continue to the next sprint unless blocked by a genuine external dependency or an architectural ambiguity that cannot safely be resolved.

Do not wait for the human to manually approve every file change. Make sensible implementation decisions within this specification.

---

# 2. Project Goal

Build an AI-assisted geopolitical intelligence dashboard that can ingest and correlate heterogeneous geopolitical observations such as:

- news/RSS articles
- structured geopolitical event feeds
- satellite thermal observations
- manually submitted observations
- recorded demo/replay data

The system should process those observations through a robust asynchronous pipeline:

```text
External Source
      ↓
Ingestion Adapter
      ↓
Raw Observation
      ↓
Normalisation
      ↓
Processing Queue
      ↓
AI Enrichment
      ↓
Schema Validation
      ↓
Deterministic Location Resolution
      ↓
Deduplication
      ↓
Incident Correlation
      ↓
Persistence
      ↓
Domain Event
      ↓
SignalR
      ↓
CesiumJS Dashboard
```

The key domain distinction is:

> **An observation is not necessarily an incident.**

For example:

```text
Reuters article
Al Jazeera article
NASA FIRMS observation
ACLED record
        ↓
  same underlying event
        ↓
GeopoliticalIncident
```

This correlation capability is a core part of the project.

---

# 3. Career / Portfolio Objective

The finished repository must be credible as a demonstration project for roles such as:

- Senior Software Engineer
- Senior .NET Engineer
- Staff-track .NET Engineer
- AI Engineer
- Applied AI Engineer
- AI/ML Software Engineer
- Platform Engineer with AI responsibilities

The code should therefore demonstrate:

- architecture and separation of concerns
- dependency inversion
- asynchronous processing
- concurrent/background workloads
- data modelling
- spatial querying
- resilient external integrations
- AI integration with validation and observability
- AI evaluation rather than blind trust
- classical ML alongside LLMs
- testing
- operational telemetry
- CI/CD
- Docker
- clear architectural trade-offs

The project should be technically credible even when live external data is unavailable.

---

# 4. Technology Stack

## Runtime

- .NET 10 LTS
- ASP.NET Core 10
- C# 14
- Entity Framework Core 10

## Architecture

- Modular monolith
- Clean Architecture principles
- Domain/Application/Infrastructure/API separation

## Backend

- ASP.NET Core Minimal APIs
- SignalR
- `System.Threading.Channels`
- BackgroundService / hosted services
- `HttpClientFactory`
- .NET resilience pipeline

## Database

- SQLite for local deployment and simplicity
- SpatiaLite for meaningful spatial queries

## Frontend

Use **CesiumJS** as the primary visualisation technology.

Use:

- HTML
- CSS
- JavaScript
- CesiumJS
- Chart.js

Do not simultaneously introduce MapLibre unless a concrete requirement emerges. The project is intended to present a global 3D geopolitical view, so CesiumJS is preferred.

## AI

- Microsoft.Extensions.AI
- local Ollama provider for development/demo
- optional OpenAI/Azure OpenAI provider
- provider-independent abstractions
- embeddings for semantic similarity/correlation where available

## ML

Use an appropriate .NET-compatible classical ML library. Prefer a lightweight model and reproducible evaluation rather than a complex model that adds little value.

## Observability

- OpenTelemetry
- structured logging
- metrics
- distributed traces / activities

## Testing

- xUnit
- unit tests
- integration tests
- fixture/contract tests
- AI evaluation tests

## DevOps

- Docker
- GitHub Actions

---

# 5. Solution Structure

Create/maintain:

```text
GeopoliticsDashboard.sln

src/
    Geopolitics.Api/
    Geopolitics.Application/
    Geopolitics.Domain/
    Geopolitics.Infrastructure/
    Geopolitics.Workers/

tests/
    Geopolitics.UnitTests/
    Geopolitics.IntegrationTests/
    Geopolitics.AiEvaluationTests/

docs/
    architecture.md
    adr/
        001-modular-monolith.md
        002-database.md
        003-processing-queue.md
        004-ai-provider-abstraction.md
        005-location-resolution.md
        006-event-correlation.md
        007-replay-mode.md
        008-signalr.md
        009-ml-model.md

PROJECT_SPEC.md
CLAUDE.md
README.md
Dockerfile
docker-compose.yml (where useful)
```

Frontend can initially live under:

```text
src/Geopolitics.Api/wwwroot/
```

Do not introduce a separate frontend project unless it genuinely improves the architecture.

---

# 6. Dependency Direction

The intended dependency direction is:

```text
Domain
  ↑
Application
  ↑
Infrastructure
  ↑
API / Workers
```

More precisely:

- Domain depends on nothing external.
- Application depends on Domain and abstractions.
- Infrastructure implements Application abstractions.
- API depends on Application and Infrastructure composition where appropriate.
- Workers depend on Application and Infrastructure composition where appropriate.

The Domain project must not reference ASP.NET Core, EF Core infrastructure types, SignalR, HTTP clients, or AI SDKs.

---

# 7. Domain Model

The domain should distinguish between observations, incidents, locations, and AI assessments.

Initial concepts:

```text
GeopoliticalIncident
RawObservation
NewsObservation
SatelliteObservation
ExternalEventObservation
DataSource
GeoLocation
LocationResolution
AIInference
EntityReference
IncidentCorrelation
```

Enums/value objects should be used where they improve correctness and clarity.

Suggested event types:

```text
Conflict
MaritimeIncident
NavalIncident
Piracy
Terrorism
Protest
Sanctions
MilitaryMovement
CyberIncident
NaturalHazard
Other
```

Suggested severity:

```text
Low
Medium
High
Critical
Unknown
```

Do not over-model the domain purely for theoretical purity. Add concepts when they represent genuine business/domain behaviour.

---

# 8. Core Event Lifecycle

The system must support:

```text
Raw Observation
    ↓
Normalised Observation
    ↓
Enriched Observation
    ↓
Validated Observation
    ↓
Location Resolved
    ↓
Duplicate/Correlation Assessment
    ↓
Associated with Incident
    ↓
Persisted
```

Keep processing status visible enough to diagnose failures.

A failed AI call or geocoder should not silently destroy the source observation.

---

# 9. AI Architecture

The AI subsystem must use `Microsoft.Extensions.AI` as the abstraction layer where suitable.

The application must not directly depend on one specific model vendor.

Support configuration such as:

```text
Ollama
OpenAI
Azure OpenAI
Mock/Test provider
```

The application should be able to switch provider/model through configuration rather than application-code rewrites.

Suggested abstractions:

```csharp
IEventEnrichmentService
IAiProviderFactory (only if genuinely needed)
IEmbeddingService / IEmbeddingGenerator integration
ILocationResolver
```

Do not wrap every AI SDK type in needless abstractions. Use interfaces around actual domain/application behaviour.

---

# 10. AI Enrichment Responsibilities

The LLM should be used for semantic tasks including:

- language/translation support
- summary generation
- entity extraction
- event type classification
- severity assessment
- location name extraction
- concise severity rationale
- semantic enrichment

The model should return strongly structured output.

Example:

```json
{
  "schemaVersion": 1,
  "language": "ar",
  "summary": "English summary",
  "eventType": "MARITIME_INCIDENT",
  "severity": "HIGH",
  "confidence": 0.86,
  "locations": [
    {
      "name": "Port of Aden",
      "country": "Yemen",
      "type": "PORT"
    }
  ],
  "entities": [
    {
      "name": "Example Organisation",
      "type": "ORGANISATION"
    }
  ],
  "severityRationale": "A reported exchange of fire involved two vessels."
}
```

The exact schema may evolve, but it must be versioned.

---

# 11. AI Trust Boundaries

AI output is **untrusted input**.

The pipeline must:

1. invoke the model
2. validate the response against the schema
3. reject/repair/retry invalid output
4. record the inference outcome
5. only then continue processing

Do not silently accept malformed or incomplete responses.

Do not store unrestricted chain-of-thought.

Store only concise structured rationale intended for application use.

---

# 12. Deterministic Geolocation

This is a deliberate architecture rule.

The LLM extracts a location name.

It does not become the authoritative source of latitude/longitude.

Example:

```text
LLM
 ↓
"Port of Aden"
 ↓
ILocationResolver
 ↓
Geocoder / Gazetteer
 ↓
Latitude / Longitude
```

Store resolution metadata such as:

- extracted name
- resolved name
- provider
- confidence
- timestamp

If the resolver fails, retain the observation and represent location as unresolved instead of inventing coordinates.

---

# 13. AI Traceability

Persist sufficient information to understand how an inference was generated:

- provider
- model
- prompt version
- schema version
- input reference
- structured output
- success/failure
- confidence
- latency
- created timestamp

Avoid unnecessarily storing the entire raw source repeatedly.

Do not store secrets.

---

# 14. AI Evaluation

Create a labelled evaluation dataset under:

```text
tests/data/ai-evaluation/
```

Each fixture should include source material and expected labels where practical.

Evaluate:

- event classification
- severity
- location extraction
- entity extraction
- structured output success rate
- latency

Prefer precision/recall/F1 where appropriate rather than relying entirely on exact-match accuracy.

The evaluation harness must be deterministic enough to detect regressions. Where model non-determinism exists, make that explicit.

Do not publish invented metrics.

README metrics must be generated from actual test/evaluation runs.

---

# 15. Classical ML Component

Add one small conventional ML capability to complement the LLM.

Preferred implementation:

```text
Severity prediction
```

Potential features:

- event type
- source count
- source confidence
- extracted entities
- keywords/features
- time/recency
- location-related features
- embeddings where useful

The ML implementation must expose a clean application abstraction.

Store model version metadata.

Evaluate against labelled data.

Compare:

```text
LLM severity
ML prediction
Labelled/human expected severity
```

Do not claim that either model is objectively determining geopolitical danger. Clearly document limitations and dataset bias.

---

# 16. Semantic Deduplication

Implement deduplication in layers:

### Layer 1 — Exact source identifier

URL, provider ID, or equivalent.

### Layer 2 — Normalised content hash

Normalise text and calculate a deterministic hash.

### Layer 3 — Semantic similarity

Use embeddings where available to identify strongly similar observations even when wording and URLs differ.

Potential duplicates should generally be retained as source observations.

Do not blindly delete evidence.

Instead assess whether an observation should be linked to an existing incident.

---

# 17. Incident Correlation

The system must be able to associate multiple observations with one real-world incident.

Correlation should consider:

- temporal proximity
- geographic proximity
- event type
- organisations/entities
- semantic similarity

Thresholds must be configurable.

Example configuration:

```text
MaxTimeDifference
MaxDistanceKm
SemanticSimilarityThreshold
```

Make the correlation logic testable in isolation.

---

# 18. Spatial Data

Use SpatiaLite where useful.

Do not use spatial technology merely as a storage format for latitude/longitude.

Implement actual spatial queries such as:

- incidents within X km of a point
- incidents near a maritime chokepoint
- incident density within a region
- nearby observations

The system must gracefully handle the possibility that spatial native dependencies are unavailable in an unsupported local environment.

Document setup requirements.

---

# 19. Ingestion Architecture

All external sources must be represented by adapters/interfaces.

Examples:

```text
IEventSource
IRssFeedProvider
INasaFirmsProvider
IAcledProvider
```

Do not let vendor-specific code leak throughout the application.

All providers should eventually yield a common/raw observation representation that can enter the same processing pipeline.

---

# 20. External Sources

Implement, where practical:

## RSS/news

Use RSS/Atom feeds from suitable public sources.

Do not make one specific publisher a hard architectural dependency.

## NASA FIRMS

Use NASA FIRMS as a satellite thermal anomaly source where credentials/configuration permit.

The integration must be optional and resilient to rate limits/API failures.

## ACLED

Treat ACLED as an optional authenticated provider.

The base application and CI must still work without an ACLED credential.

Never commit credentials.

---

# 21. Provider Configuration

Support configurations such as:

```text
Demo
Live
```

Demo mode should require no external API keys.

Live providers should be enabled individually through configuration.

Use standard .NET configuration mechanisms and environment variables/user secrets as appropriate.

---

# 22. Replay / Demo Mode

This is a mandatory feature.

Create a deterministic replay mode using recorded/sanitised sample observations.

For example:

```bash
dotnet run -- --mode replay
```

or an equivalent configuration approach.

Replay should simulate a realistic event stream including:

- multiple source types
- AI processing
- delays
- duplicate reports
- correlated observations
- realtime dashboard updates

This must allow a polished demo without depending on live external APIs.

---

# 23. Async Processing

SignalR is not the processing backbone.

Use an application-level queue abstraction implemented initially with `System.Threading.Channels`.

Conceptually:

```text
Ingestion
   ↓
Channel Queue
   ↓
Background Processor
   ↓
Enrichment
   ↓
Persistence
```

The queue should support:

- cancellation
- backpressure where appropriate
- controlled concurrency
- graceful shutdown
- observable queue health

Add an ADR explaining the choice.

Do not introduce an external broker unless scale requirements genuinely justify it.

---

# 24. SignalR Architecture

SignalR is the realtime presentation mechanism.

Correct sequence:

```text
Ingest
 ↓
Process
 ↓
Validate
 ↓
Persist
 ↓
Publish
 ↓
SignalR
 ↓
Browser
```

The pipeline must be able to run without any connected SignalR clients.

Clients should receive messages/events such as:

- incident created
- incident updated
- incident correlated
- observation received
- satellite observation available

---

# 25. Resilience

External integrations must support appropriate combinations of:

- timeout
- cancellation
- retry
- exponential backoff
- HTTP 429 handling
- circuit breaker
- malformed payload handling
- structured logging

Do not blindly retry non-transient failures.

A failing provider must not crash unrelated workers or the whole application.

Test failure paths.

---

# 26. Observability

Use OpenTelemetry and structured logs.

Instrument important pipeline stages.

Suggested metrics:

```text
ingestion.items.received
ingestion.items.failed
ingestion.provider.latency
pipeline.items.processed
pipeline.items.failed
ai.requests
ai.failures
ai.validation_failures
ai.latency
geocoding.success
geocoding.failure
events.created
events.correlated
events.deduplicated
signalr.publications
```

Where practical, propagate correlation/trace identifiers through the pipeline.

The goal is to make a single observation traceable from ingestion through enrichment, storage, and realtime delivery.

---

# 27. Security

Apply sensible production practices:

- never commit secrets
- validate external input
- validate URLs/remote fetch targets
- guard against unrestricted SSRF patterns
- validate request payloads
- cap input sizes
- avoid unbounded queue growth
- avoid excessive AI spending/API usage
- use configuration for credentials
- log safely without leaking secrets

No authentication system is required unless a specific feature genuinely needs one. Do not build an elaborate identity system merely for the portfolio.

---

# 28. API Design

Use Minimal APIs with clear request/response DTOs.

Suggested endpoints:

```text
GET  /api/incidents
GET  /api/incidents/{id}
GET  /api/incidents/nearby
GET  /api/analytics/summary
GET  /api/analytics/timeseries
POST /api/observations
GET  /api/health
```

Add suitable validation and error handling.

Use OpenAPI support where practical.

Do not expose EF entities directly as your long-term API contract if that creates unnecessary coupling.

---

# 29. Frontend

The UI should present a professional intelligence-dashboard aesthetic rather than a generic CRUD website.

Core components:

- full-screen 3D globe
- incident markers
- event severity visualisation
- maritime routes/chokepoints
- satellite hotspot layer
- live feed
- event details drawer
- source list
- event timeline
- filters
- time range controls
- analytics widgets
- confidence indicators

Avoid excessive animation that makes the dashboard harder to use.

The UI should communicate uncertainty rather than imply AI outputs are facts.

Example:

```text
HIGH
84% model confidence
```

rather than presenting AI-generated classification as absolute truth.

---

# 30. Analytics

Implement queries for:

- total incidents in last 24h
- incidents over time
- incidents by severity
- incidents by event type
- incidents by region
- maritime threat activity
- satellite hotspot activity

Support time windows:

```text
24h
7d
30d
90d
```

Implement a heuristic **Geopolitical Activity Score**.

The calculation must be documented.

Potential components:

```text
severity
recency
source confidence
observation count
event frequency
```

Clearly label it as a heuristic/analytical score, not an objective geopolitical truth.

---

# 31. Performance

The project should demonstrate reasonable engineering under realistic data volumes.

Consider:

- database indexes
- pagination
- efficient spatial queries
- bounded queues
- controlled background concurrency
- caching where justified
- avoiding N+1 queries
- batch processing where appropriate

Add basic performance/load tests around the processing pipeline.

Do not optimise prematurely.

Measure before making strong performance claims.

---

# 32. Testing Strategy

Testing begins in Sprint 1 and continues throughout the project.

## Unit tests

Test:

- domain behaviour
- validation
- deduplication
- correlation
- scoring
- severity rules
- queue behaviour
- location handling

## Integration tests

Test:

- API
- EF Core persistence
- migrations
- ingestion pipeline
- SignalR publication
- analytics
- spatial querying

Use the real SQLite/SpatiaLite behaviour where the production semantics matter.

## Provider fixture tests

Use recorded fixtures for:

- RSS
- NASA FIRMS
- ACLED

## AI evaluation tests

Test:

- structured output validation
- extraction/classification
- regression fixtures
- failure handling

## Failure-path tests

Explicitly test:

- AI malformed JSON
- AI timeout
- provider failure
- HTTP 429
- malformed feed
- geocoder unavailable
- duplicate observation
- conflicting observations
- shutdown/cancellation

---

# 33. CI/CD

GitHub Actions should run on pull requests and/or pushes.

At minimum:

```text
dotnet restore
dotnet build
dotnet test
dotnet format --verify-no-changes
```

Where practical, also run:

- static analysis
- Docker build

CI should not require paid third-party credentials for standard verification.

---

# 34. Docker

Provide a working Docker build.

Aim for:

```bash
docker build -t geopolitics-dashboard .
```

and where useful:

```bash
docker compose up
```

Local demo should work without secrets.

Document how to enable live providers separately.

---

# 35. Documentation

Maintain:

## README.md

Must explain:

- project overview
- screenshots/demo
- architecture
- technology stack
- event lifecycle
- AI architecture
- ML architecture
- spatial processing
- ingestion providers
- resilience
- observability
- testing
- evaluation methodology
- limitations
- local setup
- demo/replay mode
- live provider configuration
- scaling discussion

## docs/architecture.md

Include:

- component diagram
- data-flow diagram
- AI pipeline
- event correlation model
- explanation of major boundaries

## ADRs

Create Architecture Decision Records for important decisions.

At minimum:

1. Modular monolith
2. SQLite/SpatiaLite
3. Channels for async processing
4. Microsoft.Extensions.AI provider abstraction
5. Deterministic geolocation separation
6. Observation vs incident model/correlation
7. Replay mode
8. SignalR after persistence
9. Classical ML alongside LLM

---

# 36. Git History

Make commits small, logical, and meaningful.

Prefer messages such as:

```text
feat: establish modular application architecture
feat: introduce persistent geopolitical domain model
feat: add asynchronous ingestion pipeline
feat: add realtime incident publication
feat: introduce AI enrichment abstraction
feat: validate structured AI inference
feat: add deterministic location resolution
feat: add semantic deduplication
feat: introduce incident correlation
feat: add spatial querying
feat: add live OSINT adapters
feat: add replayable ingestion mode
feat: introduce geopolitical activity scoring
feat: add severity prediction model
feat: add AI evaluation harness
feat: add OpenTelemetry instrumentation
test: add resilience and failure-path coverage
docs: document architecture decisions
chore: add CI and container deployment
```

Do not squash all project history into a single giant commit.

---

# 37. Sprint Plan

## Sprint 1 — Foundation & Architecture

### Goal

Build the production-style application foundation and visual shell.

### Implement

- .NET 10 solution
- project boundaries
- domain foundations
- Application abstractions
- EF Core
- SQLite
- migrations
- seed/demo data
- Minimal API
- health checks
- structured logging
- OpenTelemetry foundation
- CesiumJS globe
- basic incident markers
- basic details drawer
- GitHub Actions
- Dockerfile
- initial README
- architecture documentation
- initial ADRs
- initial unit/integration tests

### Definition of Done

```text
dotnet build
PASS

dotnet test
PASS
```

App runs locally with no external credentials.

### Commit

```text
feat: establish modular application architecture
```

---

## Sprint 2 — Asynchronous Event Processing

### Goal

Create the internal event pipeline and realtime delivery.

### Implement

```text
IEventSource
ReplayEventSource
Channel Queue
Background Processor
Normalisation
Persistence
Deduplication
SignalR
Live Feed UI
```

Add metrics/logging for each meaningful stage.

SignalR publication happens only after successful persistence.

Add graceful shutdown and cancellation.

Add tests for success and failure paths.

### Definition of Done

A replayed event can travel through the pipeline and appear on the dashboard without page refresh.

### Commit

```text
feat: introduce asynchronous event processing pipeline
```

---

## Sprint 3 — AI-Assisted OSINT Enrichment

### Goal

Integrate provider-independent AI safely.

### Implement

- Microsoft.Extensions.AI
- Ollama support
- optional OpenAI/Azure provider
- `IEventEnrichmentService`
- structured output schema
- schema validation
- retry/repair handling
- AI inference persistence
- prompt/schema versioning
- confidence
- translation
- entity extraction
- event classification
- severity classification
- location name extraction
- deterministic location resolver
- manual observation endpoint
- frontend submission form
- AI telemetry
- mock AI provider for tests/demo
- initial evaluation fixtures

### Definition of Done

A manually submitted foreign-language observation can be translated/enriched, validated, geolocated through a deterministic resolver, persisted, and delivered to the dashboard.

The application must still run using a mock/demo AI provider.

### Commit

```text
feat: introduce AI-assisted OSINT enrichment pipeline
```

---

## Sprint 4 — Live OSINT & Spatial Intelligence

### Goal

Add real data providers and real spatial/correlation behaviour.

### Implement

- RSS adapter
- NASA FIRMS adapter
- optional ACLED adapter
- HttpClientFactory
- resilience
- rate-limit handling
- fixture tests
- SpatiaLite
- spatial queries
- semantic deduplication
- embeddings
- incident correlation
- temporal/geographic/entity/similarity correlation
- maritime chokepoints
- satellite heatmap
- live feed filters
- replay dataset that exercises multi-source correlation

### Definition of Done

Multiple source types enter the same pipeline and can be associated with the same underlying incident.

The system remains usable when live providers are disabled or unavailable.

### Commit

```text
feat: add live OSINT ingestion and spatial correlation
```

---

## Sprint 5 — Analytics & Classical ML

### Goal

Add measurable analytical intelligence beyond the map.

### Implement

- 24h/7d/30d/90d analytics
- severity distributions
- event type distributions
- geographic activity
- maritime threat analysis
- activity timeseries
- Geopolitical Activity Score
- classical ML severity model
- labelled training/evaluation dataset
- model versioning
- prediction endpoint/service
- ML evaluation
- compare LLM vs ML vs labels

### Definition of Done

The dashboard exposes meaningful analytical views and the repository contains reproducible evidence of model performance.

No invented metrics.

### Commit

```text
feat: add geopolitical analytics and severity model
```

---

## Sprint 6 — Production Quality & Portfolio Release

### Goal

Turn the complete system into a polished portfolio project.

### Implement

- complete OpenTelemetry
- meaningful metrics
- traces
- structured operational logging
- resilience verification
- cancellation verification
- performance tests
- CI improvements
- Docker improvements
- polished UI
- source/evidence display
- timeline
- confidence display
- replay demo
- sample dataset
- architecture diagrams
- AI pipeline diagrams
- final README
- ADRs
- security review
- dependency review
- compiler warning cleanup
- dead code cleanup
- formatting
- integration test expansion

### Final verification

```bash
dotnet build
dotnet test
dotnet format --verify-no-changes
docker build -t geopolitics-dashboard .
```

Only report results that actually occurred.

### Commit

```text
chore: prepare production-quality portfolio release
```

---

# 38. Final Architecture Review

After Sprint 6, perform a hostile senior/principal engineer review.

Do not merely praise the repository.

Review:

1. Architecture
2. SOLID principles
3. dependency direction
4. domain modelling
5. async processing
6. concurrency
7. database design
8. spatial modelling
9. external integration design
10. resilience
11. observability
12. security
13. testing
14. AI architecture
15. AI evaluation
16. ML implementation
17. frontend/backend separation
18. performance
19. maintainability
20. documentation

Look specifically for:

- unnecessary abstractions
- over-engineering
- hidden coupling
- anemic domain models
- inappropriate AI usage
- hallucination risk
- missing validation
- incorrect SignalR architecture
- unreliable workers
- race conditions
- poor cancellation
- poor shutdown behaviour
- missing DB indexes
- N+1 queries
- test gaps
- secret handling
- brittle feed integrations
- fabricated data/metrics

Rank issues:

```text
Critical
High
Medium
Low
```

For each issue explain:

- why it matters
- where it occurs
- recommended fix

Then implement genuinely justified fixes.

Do not make cosmetic changes merely to create more code.

Finally rerun all verification commands.

---

# 39. Human/Agent Working Model

This is an AI-assisted portfolio project.

The human developer owns architectural intent.

The coding agent owns implementation execution.

Major architectural decisions must be recorded.

The project should remain understandable and defensible by the human developer.

The agent should favour explanations such as:

> "I chose Channels because this is currently a single-node deployment and Channels provide an inexpensive bounded in-process queue. The queue abstraction allows migration to a durable external broker later if scale/reliability requirements justify it."

rather than:

> "I used Channels because they are fast."

The project must demonstrate engineering judgement, not technology accumulation.

---

# 40. Interview Narrative the Project Should Enable

At the end of the project, the developer should be able to explain the system roughly as follows:

> I built a .NET 10 modular monolith that ingests heterogeneous geopolitical data through adapter-based background workers. I designed an asynchronous processing pipeline using Channels so ingestion is decoupled from UI delivery and external systems.
>
> I used Microsoft.Extensions.AI for provider-independent AI enrichment, but deliberately kept LLMs away from authoritative geospatial resolution. The model extracts semantic location information and a deterministic geocoder resolves the actual coordinates.
>
> I added semantic deduplication and incident correlation so multiple articles and satellite observations can represent one underlying geopolitical event.
>
> I built an AI evaluation harness instead of assuming the LLM was accurate, and added a conventional ML model so I could compare LLM predictions with a trained model and labelled data.
>
> The system is instrumented with OpenTelemetry, includes resilience around external providers, supports deterministic replay for demonstrations, and has unit, integration, provider-fixture, and AI evaluation tests.

The repository should support that narrative with actual code, tests, documentation, metrics, and design decisions.

---

# 41. Final Success Criteria

The project is complete when all of the following are true:

- It is a .NET 10 application.
- Architecture is a coherent modular monolith.
- Domain/application/infrastructure boundaries are respected.
- Events flow asynchronously through a real processing pipeline.
- SignalR is decoupled from processing.
- AI enrichment uses Microsoft.Extensions.AI or an equivalent provider abstraction.
- AI output is schema validated.
- AI inference is observable and traceable.
- Location names are resolved deterministically rather than hallucinated by the LLM.
- Multiple observations can correlate into one incident.
- Spatial queries are real rather than cosmetic.
- At least one real external source works where configured.
- Demo/replay mode works without credentials.
- Semantic similarity is used meaningfully where appropriate.
- At least one classical ML component exists.
- AI/ML evaluation is reproducible and contains real measurements.
- External integrations are resilient.
- OpenTelemetry instrumentation exists.
- Tests cover success and important failure paths.
- CI passes.
- Docker build works.
- Documentation explains architecture and trade-offs.
- The frontend is polished enough for a portfolio demonstration.
- No secrets or fabricated metrics are committed.
- The developer can explain every major architectural decision.

---

# 42. Start Here

If this repository is empty, begin with Sprint 1.

If the repository already contains partial implementation, inspect it first and reconcile it with this specification rather than destroying existing useful work.

Do not ask the human to manually provide the sprint requirements again.

Use this document as the source of truth.

Begin by:

1. Inspecting the repository.
2. Checking installed .NET SDKs.
3. Checking whether a solution/project already exists.
4. Creating/updating `CLAUDE.md`.
5. Creating the Sprint 1 foundation.
6. Running build and tests.
7. Continuing through the remaining sprints in order.

At the end of each major increment, report:

```text
Implemented:
...

Tests:
...

Verification:
...

Architecture decisions:
...

Known limitations:
...

Next sprint:
...
```

**Do not fabricate any part of this report.**
