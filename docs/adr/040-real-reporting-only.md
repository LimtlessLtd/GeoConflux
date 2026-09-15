# ADR 040: The system carries real reporting only, and has no synthetic source to fall back on

- Status: Accepted
- Date: 2026-09-15
- **Supersedes [ADR 007](007-replay-mode.md)**, which added a recorded replay source so a
  demonstration would not depend on provider uptime or credentials. That need is met; the cost is
  not worth paying any longer. See *What changed since ADR 007* below.
- **Does not touch [ADR 013](013-deterministic-provider-by-default.md).** The deterministic *model*
  stand-in is a different thing from a synthetic *data source*, and it stays. See *What this is not*.
- Depends on [ADR 025](025-agent-collected-osint.md) and the live adapters for the data that remains.

## Context

Two components in this repository invented observations.

`ReplayEventSource` read an embedded `replay-observations.json` and emitted eleven fabricated
records — five invented wire services, a scenario about Bab-el-Mandeb — as a registered
`IEventSource`, on every run, unless a setting turned it off. `DemoDataSeeder` wrote two illustrative
incidents straight into an empty database so a first run had something on the globe.

Both were scrupulously labelled. Every replay record carried `ObservationProvenance.Recorded`, a
`replay:` source prefix, and an `isDemo` flag the dashboard rendered as a banner. Nothing was ever
passed off as real.

That is not the problem. The problem is that they were there at all.

### What changed since ADR 007

ADR 007 was written in Sprint 2, when the pipeline had nothing else to read. A portfolio
demonstration that depended on provider uptime would have been blank as often as not, and a recorded
stream was the honest way to show a working pipeline with no credentials.

The published snapshot of 2026-09-15 contained **236 observations: 198 polled live from ten public
RSS feeds, 27 gathered into committed collection bundles, and 11 replayed**. The recorded stream is
now 4.7% of the page and the only part of it that describes nothing real. The argument that justified
it — *without this there is nothing to show* — has been false for some time.

What remains is the cost. A system whose entire claim is that it reports what sources actually said
should not contain a component whose job is to make things up, however well labelled. The label is a
mitigation for a risk that does not need to exist, and mitigations rot: the previous ADR's promise
that replay data is "clearly labelled" was kept, but it required every downstream consumer to keep
honouring it, for ever, including ones not yet written.

## Decision

**Nothing in the application can produce a synthetic observation.**

Not disabled by configuration — absent. `ReplayEventSource`, `ReplayOptions`,
`replay-observations.json`, `DemoDataSeeder` and `SeedOptions` are deleted, along with their
registrations and their `Replay:` and `Seed:` configuration blocks. There is no setting that brings
them back, because there is nothing left for a setting to switch on.

An empty database now stays empty until a source reads something real.

### The labelling stays

`ObservationProvenance.Recorded`, `RawObservation.IsDemo`, the dashboard's demo banner, and the
snapshot's `isDemoData` flag are all retained, and the envelope still defaults to `Recorded` when an
adapter does not state its provenance.

This is deliberate and is not vestigial. The labelling is the safety net, and a net removed the
moment it stops catching anything is not a net. Keeping the default means a future adapter that
forgets to declare itself fails *towards* being marked synthetic rather than away from it, and the
deploy fails the build on a snapshot containing any such record. Removing the machinery would make
the one dangerous direction — fabricated data appearing unlabelled — the silent one.

### `ProviderMode.Demo` becomes `ProviderMode.Offline`

The mode meant "recorded data only, no external call". With no recorded data, half that sentence is
false, and a mode named `Demo` that produces nothing is a promise the code does not keep. It is
renamed for what it does. The default is unchanged: a clone of this repository still makes no network
call and needs no credential — it simply also ingests nothing, which is the honest answer when a
system that carries only real reporting is forbidden to fetch any.

### The deploy loses its floor

`pages.yml` kept the replay stream enabled so a day when every feed was unreachable still published a
working pipeline rather than failing. That floor is gone. A run that ingests nothing real now exports
nothing, fails verification, and leaves the previous page live.

That is the better failure. A stale page that was true yesterday beats a fresh one padded with
invented records, and the workflow now says so where it used to say the opposite. It also fails the
build outright if the snapshot carries any synthetic observation, which — since nothing can produce
one — means that check fires only if somebody reintroduces a fabricated source.

### The crafted corpus moves to the test project

The eleven records were not only demonstration data. They were a deliberately-shaped fixture: a
byte-identical redelivery, two outlets describing one event, a place name no gazetteer resolves, and
a provider supplying its own coordinates. Twelve integration tests drove the composed host through
those cases, and real feeds cannot be relied upon to contain any of them on a given day.

So the fixture survives as `ScriptedEventSource` under `tests/`, registered only when a test asks for
it by name. Deleting it outright would have quietly traded the user's request for a loss of coverage
they did not ask for; confining it to the test project achieves the actual goal — the *product*
cannot fabricate — without paying that price. Its records are still marked `Recorded`, because a
fixture that presented itself as live reporting would let an assertion pass for the wrong reason.

## Consequences

**A fresh clone shows an empty dashboard.** With `Providers:Mode` at `Offline` and no credential,
there is nothing to ingest and nothing to display. That is the correct result and the README now says
so rather than promising a populated demo.

**The published page depends entirely on upstream availability.** Ten public RSS feeds and the
committed bundles are what it has. If they all fail, the deploy fails and the previous page stays up.

**11 records and 2 seeded incidents leave the published snapshot**, out of 236 and 188. The page is
materially unchanged and every record on it is now something a source actually published.

**One behavioural check was lost and is not replaced.** `ReplayIsDeterministicAcrossRuns` asserted
that the recorded source emitted identical output on repeated runs. It described a property of a
component that no longer exists; the scripted fixture is read from a file and its determinism is not
a claim the system makes about itself.

## What this is not

This does not change the AI provider. `DeterministicMockChatClient` stays, and is a different kind of
thing: it does not invent observations, it declines to enrich real ones it cannot read, and it says
so in its own output. A deployment with no model credential still ingests, deduplicates, locates and
correlates real reporting — it simply classifies with a keyword heuristic and does not translate.
That trade-off is ADR 013's, and it is unaffected by this one.
