# ADR 015: Live OSINT providers are adapters behind two switches, verified by fixtures

- Status: Accepted
- Date: 2026-09-11

## Context

The system had one ingestion source: a recorded replay stream. The specification asks for real
providers — RSS/news, NASA FIRMS thermal anomalies, and ACLED — while also requiring that the
application run, demo, and pass CI with no credentials and no network, and that no single publisher
become an architectural dependency.

Three tensions had to be resolved.

The first is that "optional integration" is easy to claim and easy to get wrong. A provider that is
merely unconfigured still tends to be *reachable*: one environment variable copied from an example,
and a demo deployment starts making outbound requests.

The second is that a live integration cannot be verified the way the rest of the system is. There is
no credential in CI, and even with one, a test that depends on a third party's live data is not a
test. But the parts that actually break in production — a feed whose date has the wrong weekday, a
rate limit, a provider answering an invalid key with HTTP 200 and a sentence of English — are exactly
the parts nobody writes by inspection.

The third is that these three sources are not epistemically equal. A satellite instrument reporting
where it detected heat is a measurement. A news article mentioning a strait is prose. Treating both
as "a source said so" would either grant the article coordinates it has not earned or discard the
instrument's, and both are wrong.

## Decision

**Adapters, not integrations.** Each provider implements the existing `IEventSource` and emits the
same `ObservationEnvelope` as the replay source. Nothing downstream knows a provider exists. A shared
`PollingEventSource` base holds everything that must be true of every external integration — the
cancellable poll loop, failure containment, per-provider latency and failure metrics, and suppression
of items already emitted — so a fourth adapter gets them without remembering to.

**Two switches, both required.** A provider polls only when `Providers:Mode` is `Live` *and* that
provider's own `Enabled` is true. FIRMS additionally requires a key and ACLED a key and an email,
because both answer an unauthenticated request in a way that looks like data rather than an error.
The configuration committed to this repository sets `Demo` with every provider disabled, and a test
asserts that under that configuration no HTTP request is made at all.

**Parsers are pure functions, pinned to recorded payloads.** The only provider-specific knowledge in
an adapter is its parser, and each is a `string` in, records out, with no HTTP or DI. Recorded
responses live in `tests/data/providers` and cover the well-formed case and the malformed ones.

**The resilience pipeline is real and is what the tests exercise.** `AddStandardResilienceHandler`
supplies timeout, exponential backoff with jitter, `Retry-After` handling, and a circuit breaker. The
adapter tests build the application's own container and replace only the primary transport, so the
retry policy under test is the one that ships.

**Declared means measured.** FIRMS declares its coordinates, because an instrument geolocating a
pixel is a measurement — this is the `SourceProvided` path ADR 005 already defines. ACLED declares
coordinates, category, and a severity derived from its own fatality count by a stated rule. RSS
declares nothing: a news item earns a position only by naming a place the gazetteer recognises, which
is the same path a manual submission takes.

## Consequences

- A clone of this repository makes no external call. That is now a test, not a claim.
- Enabling a provider is a configuration change. No code changes, and no other provider is affected.
- The adapters are verified against real response *shapes* without a credential or a network. They
  have not been run against the live services, and the README says so rather than implying coverage
  the repository does not have.
- Writing the fixtures found a real defect: .NET rejects an RFC 822 date whose weekday disagrees with
  the date, and feeds publish those. The parser now discards the redundant weekday, which is the
  behaviour a wrong day name deserves.
- FIRMS detections enter as `NaturalHazard` at low severity and their text states that a thermal
  detection records heat, not its cause. Most detections are agricultural burning or wildfire, and a
  dashboard that presented them as conflict indicators would be lying with real data. Their value is
  corroborative, and the correlator decides whether a signature near a reported incident is evidence.
- The circuit breaker is shared across all requests from one named client, which for the RSS adapter
  spans several unrelated hosts. Its throughput floor is therefore set above what a normal poll
  produces, so a routine cycle cannot trip it and a burst of failures still can. A per-host breaker
  would be better and is not worth a second HTTP stack at this scale.
- A provider that is slow, rate-limited, malformed, or down costs its own poll and nothing else. The
  pump service already isolates sources from one another; this adds isolation *within* a source, so
  one broken feed does not cost the other feeds their cycle.
