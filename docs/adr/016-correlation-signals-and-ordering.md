# ADR 016: Correlation scores a positional ceiling, and correlate-then-commit is serialised per category

- Status: Accepted
- Date: 2026-09-11

## Context

Correlation started as category, time proximity, and geographic distance. That is enough to merge two
wire reports that both carry coordinates, and not enough for the cases the specification actually
names: shared organisations, semantic similarity, and reports that carry no usable location at all.

Two problems had to be solved together.

**What to measure, and how honestly.** The specification asks for semantic similarity via embeddings
"where available". Nothing is available here: the default AI provider is a deterministic stand-in,
CI has no credentials, and the published snapshot is built offline. An embedding-shaped API backed by
something that is not an embedding would be the exact kind of claim this repository refuses to make.

**How to combine signals.** The obvious approach — a weighted mean over whichever signals a
particular comparison had — turned out to be wrong in a way worth recording, because it looked
right. A news article has no coordinates and a satellite detection names no actors, so normalising
over only the *available* weights seemed fair. It is not: the missing signal simply drops out of the
denominator, and two reports that merely both say "Beirut" score as high as two reports measured 0 km
apart. A test written before the implementation caught it; the first implementation scored a
place-name match at 0.78 where the intent was "under 0.5".

Separately, the pipeline carried a documented defect. Correlation read its candidate incidents and
then wrote, holding nothing across the two, so two workers processing simultaneous reports of one
event each saw an empty candidate list and each opened an incident.

## Decision

**`ITextSimilarity`, with a lexical default that says what it is.** The interface is the seam; the
shipped implementation is `LexicalTextSimilarity`, which compares the meaningful words two texts
share and reports its method as `lexical-overlap`. It uses the Otsuka-Ochiai coefficient rather than
Jaccard, because a two-line agency snap and a six-paragraph article about one event differ enormously
in length and Jaccard reads that as disagreement. An embedding-backed implementation can replace it
without the correlator changing.

**Confidence is a positional ceiling, modulated by corroboration.** What establishes that two reports
concern the same *place* sets the maximum: measured distance decays from 1.0 at zero to a place-name
match at the radius edge; a shared place name is capped lower; and content-only matching lower still.
Time, shared actors, and shared wording then scale that ceiling within a configurable floor. They can
strengthen or weaken a match; they can never manufacture one.

**Positional corroboration is required.** Time and topic alone never correlate. The one path without
a location demands shared actors *and* wording above the similarity threshold together, because
either alone is routinely true of unrelated reports in the same category.

**Incidents accumulate the actors their evidence names.** `GeopoliticalIncident.EntityKeys` is the
bounded union of every actor its observations mentioned, merged as reports are linked. Correlation
asks "does this report name anyone already involved" once per candidate on the hot path, so the union
is stored rather than recomputed from the incident's observations.

**Correlate-then-commit is serialised per event type.** `CorrelationGate` holds a semaphore per
`EventType` — the exact contention domain, since the candidate query filters by category — and the
processor holds it from the candidate read through to the commit. Enrichment and location resolution
stay outside it: they are the slow stages and touch no shared state.

## Consequences

- Every correlation decision still recomputes from stored data and states its reasoning. A logged
  rationale now reads "0.0 km apart, 1 shared actor(s), 53% wording overlap", with the positional
  evidence first because that is what established co-location.
- A wrong merge stays harder than a missed one, which is the intended asymmetry. A wrong merge
  destroys the distinction between two real events and is nearly invisible afterwards; two incidents
  that should have been one are obvious on the map.
- The race fix is verified against the defect rather than assumed. With the gate removed the
  concurrency test fails on every run; with it in place it passes on every run. That check mattered:
  the first version of the test passed either way, because EF Core's SQLite provider completes its
  reads synchronously and `Task.WhenAll` over async lambdas therefore ran them strictly sequentially.
  Reproducing the race needed real thread-pool dispatch and a release barrier.
- The gate is in-process, which is the correct scope for a modular monolith and is not a distributed
  lock. Two processor hosts against one database would reintroduce the race.
- Correlation still pre-filters candidates by `EventType`, so a satellite thermal detection cannot
  corroborate a piracy report however close it is. That is deliberate for now — the category is the
  cheapest and most reliable discriminator available — but it does mean cross-category corroboration
  is not something this system does, and the README says so rather than implying otherwise.
- The lexical measure cannot recognise a paraphrase with no shared vocabulary, or one event reported
  in two languages. Requiring positional corroboration is what keeps that gap from mattering much.
- Expanding the replay dataset to exercise these paths exposed a real defect in the offline AI
  stand-in: it joined a title and body with a plain space, so the body's first word was absorbed into
  the capitalised run ending the title and it invented actors like "Northern Transit Council
  Scheduled". Two reports about one organisation produced two different names for it, which silently
  disabled entity correlation. Fixed, with a regression test.

## Amendment, 2026-09-11: time gates, it does not corroborate

Originally the corroboration signals were wording overlap, shared actors, and temporal proximity,
the last scored as `1 - (hoursApart / correlationWindow)`.

Pointing the pipeline at live news feeds showed that signal to be worthless, and worse than
worthless in practice. A poll returns everything at once, so every candidate pair had timestamps
minutes apart inside a 24-hour window and scored ~1.0 on it. A signal that is maximal for every pair
is not evidence; it was a constant bonus applied to every comparison, and it was large enough to
carry pairs over the acceptance threshold on its own. On the first real run it merged a report about
a disease outbreak with one about a dress auction.

It had looked discriminating only because the recorded replay stream is spread across several hours
by construction. That is an artefact of how the fixture was written, not a property of ingestion.

Time is now used solely as the window gate it always also was. Corroboration comes from wording and
actors — what two reports actually say and who they name. Whether two reports arrived together says
nothing about whether they describe the same event.

The knock-on effect is that weakly-evidenced matches no longer clear the bar: a shared place name
with no shared wording and no shared actors used to correlate and now does not. That is the correct
direction. This ADR already states that the bar errs high because a wrong merge is destructive and
nearly invisible afterwards, and a quarter of every corroboration score had been a signal carrying no
information.

`Pipeline:TimeWeight` is removed. Two unit tests that asserted the old behaviour were rewritten, and
a test now asserts directly that reports sharing only a place and a timestamp do not merge.
