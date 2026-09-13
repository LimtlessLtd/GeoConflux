# Going global: what it would actually take

*Assessment written 2026-09-13, against the state of the repository at that date. Every access claim
here was checked on that day and is dated, because platform access policy changes faster than this
document does.*

The goal under discussion is a dashboard covering **every regional and local conflict worldwide**,
fed by real OSINT including X/Twitter.

This document is the honest answer to that, in the order the work actually has to happen. The short
version is that the question contains a wrong assumption worth correcting before any code is
written, and that the binding constraint is not a source at all.

---

## 1. The correction: X is not where global coverage comes from

X is a **latency** source, not a **coverage** source. It tells you about an event forty minutes
before a wire does. It does not tell you about conflicts nobody is tweeting about in a language you
are reading, which is most of them.

Three facts about it, all checked 2026-09-13:

- Unauthenticated reading is gated. A profile request answers HTTP 402, and `x.com/robots.txt`
  separately disallows the profile path. Both refusals are already recorded by the collection
  tooling and respected.
- The supported route is a **paid API credential** held by whoever deploys this. The architecture
  already anticipates exactly this: [ADR 025](adr/025-agent-collected-osint.md) states that a paid
  credential moves a Tier C source into the ordinary authenticated-adapter pattern ACLED uses, and
  [ADR 029](adr/029-corroboration-gate.md) states that X items would be `userGenerated` and subject
  to the corroboration gate like any other post.
- **I could not verify current tier pricing from here.** The pricing page renders through
  JavaScript. Whoever deploys this must check it rather than trust a figure quoted from memory, and
  this document declines to guess — the same rule that keeps inferred external results out of this
  repository.

So building the X adapter is *small* work — it mirrors ACLED, which already exists — and it is
**not** the thing that produces global coverage. It should be built, and it should be built fourth.

### What does produce global coverage

**ACLED and UCDP.** These are human-coded conflict event datasets with coordinates, covering
essentially every armed conflict on earth. ACLED's own navigation lists Africa, Asia-Pacific, Europe
and Central Asia, Latin America and the Caribbean, the Middle East, and US and Canada — that is the
world (checked 2026-09-13).

**Both adapters are already built and shipping disabled**, pinned by recorded fixtures, waiting for
a credential. ACLED was migrated to its current OAuth API in Sprint 9; UCDP GED Candidate was added
in the same sprint, carrying `where_prec` through to `LocationPrecision` so a provincial centroid is
never drawn with the confidence of a grid reference.

Turning those two on is the single largest coverage increase available to this project, it is mostly
operational rather than architectural, and the credentials are free to request for research use.

That is the first correction. **The second one is bigger.**

---

## 2. The actual constraint: nothing can be placed

The gazetteer holds **2,750 sourced places** — Ukraine 2,105, Yemen 482, Tigray 163 — plus a curated
core of roughly forty worldwide chokepoints, seas and country centroids.

A source reporting a clash in Kayin State, or Kidal, or Catatumbo, or Bajo Cauca produces an
observation this system stores, classifies, and **cannot draw**. The handoff for Sprint 9 put it
exactly right: *adding an adapter whose output cannot be placed is not progress.*

Every other item in this plan is downstream of fixing that. Here is what fixing it involves, and none
of it is trivial.

### 2.1 The data

[ADR 026](adr/026-gazetteer-sourcing.md) chose Wikidata (CC0) over GeoNames (CC BY 4.0), to avoid an
attribution obligation travelling with a committed artefact embedded in an assembly. **That decision
should be revisited for global scale**, for two reasons:

- The obligation is trivially satisfiable — a `NOTICE` file and a credit line on the page. It was
  avoided as a tidiness preference, not because it was burdensome.
- Wikidata does not scale to this. Sprint 9 already hit WDQS timeouts and truncated JSON bodies
  extracting **one country**, and had to be restructured into a two-phase query against the indexed
  box service to finish at all. Extracting the world that way is not a longer version of the same
  job; it is a different job that does not work.

Measured GeoNames dump sizes (2026-09-13):

| File | Compressed | Roughly |
| --- | --- | --- |
| `cities1000.zip` | 10.3 MB | populated places ≥ 1,000 people |
| `cities500.zip` | 13.0 MB | populated places ≥ 500 |
| `alternateNamesV2.zip` | 194.7 MB | every alternate name, every script |
| `allCountries.zip` | 401.7 MB | every feature |

The names are the point, not the places. A conflict is reported in the language it happens in, and a
Latin-only lexicon resolves none of it — Sprint 9 proved this the hard way, when three Arabic items
placed only because `اليمن` and `السودان` had been added.

### 2.2 The artefact-size decision, which needs an ADR

The largest file in this repository today is the 895 KB gazetteer extract. A global extract filtered
to populated places plus administrative centroids, with alternate names in the scripts actually in
scope, is plausibly **30–80 MB uncompressed**. That does not go in a repository comfortably.

Three options, each with a real cost:

1. **Commit a compressed binary artefact** (Brotli or LZ4 over a packed format rather than JSON),
   decompressed at startup. Keeps the build hermetic, which [ADR 026](adr/026-gazetteer-sourcing.md)
   requires and which is what lets a clone run offline with no credentials. Costs startup time and
   memory, and makes the artefact unreviewable in a diff — which is a genuine loss, because
   reviewability in a diff is one of this project's stated properties.
2. **Download at first run.** Breaks the hermetic build. Rejected once already; a bigger file is not
   a reason to reopen it.
3. **Tier it.** Commit a global-but-coarse layer (admin-1 and admin-2 centroids, ~50k entries, small)
   and commit deep layers only for theatres under active tasking. Honest, matches how the project
   already thinks about theatres, and makes the coverage panel's "not looked at" statement mean
   something concrete. **This is the one I would take**, and it is the only one where the artefact
   stays reviewable.

### 2.3 The engineering, which is not optional

**The linear scan dies, and the test already says so.** `Gazetteer.FindFirstMention` walks every
search term. Measured against the current lexicon on 2026-09-13: **0.732 ms per scan**, against a
guard of 5 ms — roughly sevenfold headroom. A global lexicon is 20–100× the term count, so that
headroom is gone at the first order of magnitude.

`GazetteerScaleTests` anticipates this precisely. Its failure message reads: *"At that cost the
offline enrichment provider becomes the slowest part of the pipeline, and the lexicon needs an index
rather than a linear scan."* Sprint 10 is where that sentence comes due. The fix is a multi-pattern
matcher — an Aho-Corasick automaton built once at startup — not a faster loop.

Worth separating, because only half the problem scales: `TryResolve`, the exact-name path, measured
**0.00009 ms** and is a hash lookup whose cost does not depend on lexicon size at all. Asking the
gazetteer about a name is free; *hunting* for names in prose is what gets expensive. Only the second
needs rebuilding.

**Ambiguity stops being a rounding error.** The three disambiguation rules in ADR 026 (same-place
merging, nested admin units, dominant population) were tuned against three theatres. Globally there
are dozens of Springfields, San Josés and Victorias, and "Java" is an island, a province and a
programming language. Resolution has to become **context-scoped**: biased by the source's country,
the brief's region, and names co-occurring in the same text. That is a design change, not a constant
to retune.

**The short-name rule gets worse.** Sprint 9 lost nine points of location-extraction precision to
villages named Sad, Rama and Gora and aliases including Luck and Mare, and fixed it with a length
and population floor. At global scale that floor will be both too strict (losing real small places)
and too loose (admitting new collisions). It needs to become a per-language decision, and it needs
the evaluation harness pointed at it so the cost is measured rather than argued about.

---

## 3. The plan, in dependency order

Each of these is a sprint in the sense the existing plan uses: it reaches `main` green, and the
published page changes.

### Sprint 10 — Global placement

The prerequisite for everything else.

- Revisit ADR 026 and record the outcome: GeoNames under CC BY with a `NOTICE`, or a scaled
  Wikidata extraction, with the evidence for whichever is chosen.
- Decide the artefact strategy (§2.2) in an ADR before writing the data — the same rule that
  governed Sprint 9.
- Replace the linear scan with a multi-pattern automaton, with the existing scale test extended to
  pin the new cost.
- Make resolution context-scoped, and measure the precision effect on the evaluation harness rather
  than asserting it.
- Extend the coverage panel's lexicon ceiling to report per country, so "we hold 12 place names for
  Myanmar" is visible as the limit it is.

**Done when** a report naming a district town in a country this project has never touched is drawn
in the right place, and the evaluation harness shows what that cost in precision.

### Sprint 11 — The comprehensive layer

Turn on what already exists.

- ACLED and UCDP live, with credentials supplied through deployment secrets, never committed. The
  Pages workflow is the deployment, so this is GitHub repository secrets plus the existing
  redaction mechanism from [ADR 021](adr/021-outbound-trust-boundary.md).
- Backfill and paging: these are datasets with history, not feeds with a window. That is a different
  ingestion shape from anything here — bounded, resumable, and idempotent against the existing
  fingerprint deduplication.
- Volume handling. A global ACLED pull is orders of magnitude more than four RSS feeds, and the
  bounded queue from [ADR 003](adr/003-processing-queue.md) will apply backpressure that was never
  exercised at this scale.

**Done when** the map shows conflict events on every populated continent, each with a coded
precision the dashboard repeats rather than flattens.

### Sprint 12 — GDELT, and a trust decision

GDELT monitors news in over 100 languages globally, is free, and its API answered on 2026-09-13
with a documented rate limit of one request per five seconds — which the access layer built in
Sprint 8 already honours, since it treats a published crawl-delay as a floor.

It is the closest free thing to "every local conflict, everywhere". It is also **machine-coded**,
and its geocoding resolves *mentions* rather than events — an article about Gaza filed from London
can geocode to both.

That is a genuine trust question and it deserves an ADR rather than a shrug. This project's rule is
that only measurements may declare their own coordinates: a satellite geolocating a pixel, and a
curated dataset publishing a coded location. **A machine-coder is neither.** My recommendation is
that GDELT enters as a corroborating source that may *propose* a place name and may not *declare* a
coordinate — the same standing a news item has — which keeps
[ADR 005](adr/005-location-resolution.md) intact and still buys the reach.

**Done when** GDELT is contributing, and an ADR records why it was not given the coordinate
authority ACLED has.

### Sprint 13 — X, and the rest of the fast layer

Now, and not before.

- An X adapter behind `IEventSource`, mirroring ACLED exactly: parser pinned by recorded fixtures,
  two switches plus a credential, dormant by default, and a test proving a default clone makes no
  request. Items enter as `userGenerated` and the corroboration gate already applies to them with no
  change.
- The credential must be a real paid subscription. An account created to present an automated
  collector as a person is not an option, whatever it would unlock — that rule does not move.
- Expand the Tier B channel lists per region. This is **editorial work, not engineering**: deciding
  which Telegram channels speak for the Sahel, or Myanmar, or Balochistan, is a judgement about
  sources and the hardest part of it cannot be automated. The brief format already holds it.

**Done when** X items appear, are visibly claims, and cannot form an incident alone — and when the
coverage panel shows how much of the picture now rests on them.

### Sprint 14 — Scale the pipeline

Known breakages at global volume, all currently fine and all currently untested at this scale:

- **Enrichment cost.** One model call per observation does not survive a global feed. Needs
  deterministic-first triage, batching, or a cache keyed on content hash.
- **Correlation.** Candidate lookup is bounded by a time window and an event type. Globally that
  window holds far more candidates, and the current scoring is linear over them.
- **Storage.** SQLite has been right so far. Global volume with real spatial querying is
  PostGIS territory, which reopens [ADR 002](adr/002-database.md) and
  [ADR 017](adr/017-spatial-querying.md).
- **The snapshot.** The exporter writes the most recent 200 observations. A global dashboard needs
  spatial tiling or the published page becomes a sample presented as a picture.

### Sprint 15 — Honest global coverage

Extend what Sprint 8 built from "countries that appear in the table" to a declared watch list.

Today, a country absent from the region table is reported as *not looked at*, which is true but
coarse. At global scope the dashboard should state, per country: whether any source is aimed at it,
which ones, and what they returned. That turns the coverage panel from a count into a map of this
system's own reach — which is the only thing that makes the phrase "global dashboard" defensible.

---

## 4. What I would push back on

**"Every regional and local conflict"** is not achievable at full fidelity by this repository, and a
plan that pretends otherwise would be the same failure the coverage panel exists to prevent. The
honest ceiling is set by things no amount of engineering moves:

- **Whole regions have no readable public surface.** The dominant platform across much of Africa and
  South Asia is WhatsApp, which is end-to-end encrypted and has no public surface at all. Coverage
  there comes from regional wires and from ACLED, and it will always be thinner.
- **Local conflicts are under-reported by construction.** A land dispute in a rural district is not
  covered by any wire in any language. ACLED's local networks are the best answer that exists, and
  they are partial.
- **Recycled content and deception are unsolved here**, and [ADR 029](adr/029-corroboration-gate.md)
  says so plainly. Two channels reposting one claim verbatim will corroborate each other. Scaling
  the social layer scales that exposure.
- **Cost.** X is paid. Enrichment at global volume is paid. Neither is a blocker for the
  architecture and both are a blocker for a deployment, and the plan should be honest about which
  parts need a budget rather than a weekend.

So the target worth aiming at is not "every conflict". It is **globally tasked, honestly measured,
and deep where the data supports it** — with the panel saying exactly where it is thin. This project
is unusually well placed for that, because it already built the thing that makes the claim
falsifiable.

---

## 5. If you want one thing first

**Sprint 11.** Request ACLED and UCDP credentials and turn on the two adapters that already exist.

It is the largest single increase in coverage available, it is mostly operational, and it will
immediately make the case for Sprint 10 impossible to ignore — because the map will fill with
observations from countries the gazetteer cannot place, and the coverage panel will say so in
numbers.

Sprint 10 is the bigger and more interesting piece of engineering. Sprint 11 is what proves it is
needed.
