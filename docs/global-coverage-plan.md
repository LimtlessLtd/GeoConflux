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

Sprints 10 to 15 answer the question this document was written for — what going global would take.
Sprints 16 to 19 were added afterwards and answer a different one: what it takes for this to run
continuously as a live system rather than rebuild as a snapshot, and what of an ISW-shaped product is
honestly within reach. They are kept here because they are the same series, and because the ordering
in section 5 has to cover all of them at once.

### Sprint 10 — Global placement *(complete, 2026-09-13)*

The prerequisite for everything else. Delivered as described. Two things the sprint turned up that
the plan above did not anticipate:

- **The artefact trade was mostly an encoding artefact.** §2.2 framed the choice as size against
  reviewability. The same 78,547 places are 19.2 MB as indented JSON and 6.1 MB as one line per
  place — smaller *and* more reviewable, since a changed place becomes one changed line rather than
  twelve. Compression was not needed and would have cost the diff.
- **The coarse layer cannot be hunted for in running prose.** Holding every administrative unit on
  earth means holding thousands named after ordinary words. Measured against this repository's own
  corpora, 38 coarse names fired on English text — *Along* (Arunachal Pradesh), *Maritime* (Togo),
  *Centre* (Cameroon), *Police*, *Exchange*, *Village*, and a run of American counties. It resolves
  names it is given and does not guess them out of text.
  [ADR 033](adr/033-tiered-gazetteer-artefact.md) records both.

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

### Sprint 11 — The comprehensive layer *(complete, 2026-09-13)*

Turn on what already exists. Delivered as described, plus one defect the sprint uncovered: the
snapshot exporter filled the bounded queue to completion before draining it, which deadlocks
permanently for any run larger than the queue. See
[ADR 031](adr/031-dataset-history.md). The credentials themselves remain a deployment action.



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

### Sprint 16 — Conflicts as first-class objects

Today a "theatre" is a hard-coded country code and an optional bounding box, and there are three of
them. That cannot express "every regional and local conflict", and hand-authoring sixty of them is
not a plan.

The register should be **discovered, not invented**, and the source already arrives in the payloads
and is currently thrown away. UCDP GED carries `conflict_name`, `side_a` and `side_b` on every
record; the parser reads all three and drops them into a headline fallback. ACLED carries actors and
`disorder_type`. Cataloguing the world's armed conflicts is Uppsala's entire job, and this repository
is already parsing the field that names them.

- **Identity from coded data.** Conflicts come from UCDP and ACLED coding: global, stable, free,
  human-curated. Turning on a credential should make the dashboard learn every conflict UCDP codes,
  with no editorial work.
- **Membership is a predicate, not a box.** Country codes, bounds, **actor keys**, event types.
  Ukraine, Yemen and Tigray fit inside a box; Israel/Iran does not — it is fought in Syria, Lebanon,
  Iraq, Yemen, the Gulf, at sea, and in cyberspace. Actor-based membership is what makes it
  expressible, and it means an observation can belong to more than one conflict. A Houthi strike on
  shipping is Yemen *and* Israel/Iran. Counts will therefore not sum to the total, and the panel has
  to say so rather than let a reader add them up.
- **AI assigns; it does not define.** The register is small and slow-moving. The assignment problem
  is large and constant: news items, Telegram posts and Bluesky posts arrive with no conflict code at
  all, and in a live deployment that is most of the volume. So the model does most of the
  categorising — it simply does not get to decide what the categories are. A model-chosen register
  would be a function of the model's exposure rather than of the world, and exposure tracks volume of
  reporting, so the conflicts most likely to be dropped are the under-reported ones. An absent
  category looks like peace. Every assignment is recorded in `AiInference` with its prompt version
  and confidence, exactly as enrichment already is.
- **A model may propose a conflict nothing codes yet**, and it enters as a claim: stored, displayed,
  labelled as model-proposed, and unable to restructure anything until corroborated. Structurally
  identical to a single-source Tier B claim, which [ADR 029](adr/029-corroboration-gate.md) already
  handles.
- **Tempo, with a denominator.** Rate of reporting is not rate of operations, and they diverge
  exactly when it matters. This repository already records the proof: the Tigray caveat in
  `Theatre.cs` notes that ACLED's Ethiopia Peace Observatory ended fortnightly updates on 1 July
  2025, roughly six months before fighting resumed in January 2026. A naive tempo line across that
  boundary draws a de-escalation at the moment of escalation. So every tempo figure carries
  contributing-source count beside it, and a change is decomposed into "more events reported" against
  "more sources reporting". When both fall together the honest output is *coverage changed; tempo
  cannot be stated*.
- **Baselines are per-conflict and rolling**, never cross-conflict. "Ukraine 72, Tigray 9" reads as
  "Tigray is eight times quieter" when it may mean nobody is reporting Tigray.
- **Narrative, with an evidence floor.** A per-conflict summary per window is the largest thing
  missing from this dashboard and the one job a model is genuinely better at than a heuristic. It is
  also the most dangerous artefact available, because a model handed three observations writes a
  confident paragraph in the same voice as one handed three hundred. Below the floor it must decline:
  *two reports this week; too little to characterise*.

**Done when** conflicts are discovered rather than hand-authored, a thin one says so instead of
showing a number, and every AI assignment is auditable.

### Sprint 17 — The system that keeps running

The published page is a snapshot; the live system is the API host, and it already exists. It serves
the dashboard from `wwwroot`, maps the SignalR hub, and ships with `SourcesEnabled` and
`ProcessorEnabled` true — running it continuously is `dotnet run`, not a deployment project. What is
missing is everything about being left running for months rather than seconds.

- **Credentials have nowhere to live.** The workers project carries a `UserSecretsId`; the API
  project does not, so `dotnet user-secrets` does not work for the host actually being run. One line,
  and then credentials sit outside the repository where the rule requires.
- **Nothing prunes the database.** No retention policy exists anywhere. Irrelevant on day one and a
  real problem at month six with global datasets and a backfill walking backwards every night. The
  policy must respect incident linkage: pruning the evidence behind a published incident would break
  [ADR 023](adr/023-failure-boundary-and-evidence-retention.md), so what is prunable is unlinked,
  failed and duplicate material, not anything an incident rests on. SQLite also needs an explicit
  vacuum to give the space back.
- **Measure before deciding.** Row counts and file size reported alongside coverage, so retention is
  chosen against a number rather than a guess.
- **One disk is not a backup.** A scheduled copy of a WAL-mode SQLite file, done properly.
- **Surviving reboots.** `dotnet run` in a terminal dies with the terminal. A Windows service or a
  scheduled task at logon, decided and documented.
- **The downtime ledger.** The host has to know when it was not running. A per-source last-polled
  timestamp, which the `IngestionCheckpoints` table added in Sprint 11 is already shaped to hold —
  it keys on source and carries an updated-at. On startup, the gap between the newest of those and
  now is a downtime period, recorded as such.
- **Name the trigger for PostGIS** rather than a date. Sprint 14 owns the migration; this sprint owns
  writing down the measurement that would set it off.

**Done when** the host can be left running for a month unattended, and can state what it holds, what
it pruned, and when it was down.

### Sprint 18 — Closing the gaps after downtime

A machine that is switched off misses the world. The goal here is explicitly **not** comprehensive
recovery — it is that a gap is visible, partially recovered where recovery is possible, and
summarised where it is not.

The division is sharp and it decides the whole design:

- **Datasets are archives, and the gap is fully recoverable.** ACLED and UCDP still hold what
  happened while the machine was off, and Sprint 11 already built the machinery to ask for it: a
  bounded date window, narrowed where the provider could not answer in one go. Recovering a
  fortnight's downtime is pointing the existing window walk at the downtime interval. Nothing new is
  needed.
- **Flows are not recoverable.** Telegram previews, Bluesky feeds, Mastodon timelines and RSS are
  rolling windows. A busy channel from three weeks ago is simply gone, and a wire feed offers its
  last few dozen items regardless of how long you were away. No amount of engineering retrieves it.

So a downtime period produces **two different things**, and conflating them would be the failure:
recovered records, which are ordinary observations and behave like any other, and a **gap summary**,
which is not.

- **The summary is written from the recovered records only.** Never from the model's own memory of
  world events. A model asked what happened in a theatre last month will answer, fluently, from
  training data — unsourced, unverifiable, and indistinguishable from the cited material beside it.
  This is the coordinate rule ([ADR 012](adr/012-ai-output-is-untrusted-input.md)) applied to prose.
- **A gap summary is not an observation.** It creates no incident, joins no correlation, and does not
  feed tempo — it would double-count against the very records it was written from. It is stored and
  rendered as its own kind of thing, the way demo data already is.
- **It states its own asymmetry.** A summary built from datasets alone systematically under-
  represents exactly the fast-moving social material the downtime destroyed. Saying so is the
  difference between a summary and a false reassurance.
- **One summary per conflict being monitored**, which is why this follows Sprint 16 rather than
  preceding it.

**Done when** restarting after two weeks off produces, per conflict, a sourced account of what was
recovered and an explicit statement of what could not be — and none of it is mistakable for live
reporting.

### Sprint 19 — Control layers

Section 4 below records that an assessed control-of-terrain map is not something this repository can
produce, and that stands: ISW's map is made by analysts, daily, from geolocated footage, and
assessment is the product rather than a by-product. What *is* buildable, and is worth building, is
the layer underneath it — control **asserted**, with provenance, rather than control **assessed**.

Three tiers, in descending order of defensibility, and they should be built in this order:

1. **A derived actor-activity surface.** Where has a given actor been coded as active in the last N
   days. Computable today from ACLED and UCDP actor fields, needs no licence and no new source, and
   is genuinely informative. It is **not control** and must never be labelled as it — an actor
   fighting in a place is evidence about that place, not a claim to hold it.
2. **Published control geometry from named sources**, as dated polygons carrying who drew them and
   when. This is the tier the DeepState licence question blocks, and Sprint 9 deferred it whole for
   that reason. The licence has to be resolved and recorded before a line of it is written; the
   answer may be that a given source cannot be used, and that is an acceptable outcome.
3. **Claimed control from Tier B.** A post asserting a town has fallen is a claim, and the
   corroboration gate already governs exactly that shape. The hard part is placement, not policy,
   which makes this dependent on Sprint 10.

Two rules hold across all three:

- **Never render a single merged front line.** Two sources disagreeing about who holds a town is
  information, and averaging them manufactures a consensus that does not exist. Show both, dated and
  attributed. This is the corroboration gate's argument applied to geometry.
- **Control is a time series, not a state.** Assertions are dated, and "watch a conflict evolve"
  means scrubbing through them. Dated geometry at global volume is the clearest argument yet for the
  PostGIS question in Sprint 14, and this sprint should not pretend SQLite makes it comfortable.

**Done when** the map can show who is asserted to hold what, on whose authority and as of when, with
disagreement visible rather than resolved — and when the dashboard never uses the word *assessed*
about anything this system generated.

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
  parts need a budget rather than a weekend. One deployment decision moves this a long way: run the
  host on a machine you own with a local model behind [ADR 004](adr/004-ai-provider-abstraction.md),
  and enrichment's marginal cost goes to zero, which is most of what makes Sprint 14 expensive.
- **Analyst products are not pipeline products.** An assessed control-of-terrain map, a signed
  judgement of the form *X is likely attempting Y*, and the frame-by-frame geolocation of combat
  footage are all made by people. A model writes the sentence in a second; what it cannot produce is
  the institution standing behind it, and an unaccountable confident sentence is the one artefact
  this architecture has repeatedly decided against publishing. Sprint 19 builds the layer underneath
  the assessment — asserted control, with provenance and disagreement intact — and stops there. If
  the judgement layer is wanted later, the honest route is a named human writing over this data,
  which is what a good aggregation layer is for.

So the target worth aiming at is not "every conflict". It is **globally tasked, honestly measured,
and deep where the data supports it** — with the panel saying exactly where it is thin. This project
is unusually well placed for that, because it already built the thing that makes the claim
falsifiable.

That target is also not the same product as ISW, and chasing ISW would lose on every axis where they
are strong and win on none. ISW is depth by analyst in about three theatres, and it does not tell a
reader what it is *not* watching. This is breadth by pipeline everywhere, with coverage measured and
published — a panel saying "a quiet district means nobody reported, not that nothing happened" has no
ISW equivalent. Analysts do not scale to every local conflict, which is precisely why the
under-reported ones stay under-reported.

---

## 5. Order

Sprint numbers are identities, not a sequence. This is the sequence.

**Sprint 11 is done**, and it made the case it was supposed to make: the published run now places
observations into twenty-seven distinct regions, and the gazetteer covers three theatres. The gap is
no longer an argument, it is a number on the page.

Nothing else is started. In dependency order:

1. **Sprint 10 — placement.** Still the prerequisite for everything local. An event this system
   cannot draw is an event it cannot show, whatever coded it, and no amount of AI substitutes for a
   gazetteer — letting a model supply a coordinate is the one substitution this repository forbids
   outright, because a hallucinated coordinate is indistinguishable from a real one and gets drawn at
   full confidence.
2. **Sprint 16 — conflicts, tempo and narrative.** The largest visible change available, and it is
   not blocked behind placement: membership can be decided from a country code or an actor without
   any coordinate at all. A conflict whose events cannot be mapped can still be counted, and saying
   so makes the placement gap *more* visible rather than less.
3. **Sprint 17 — the system that keeps running.** Small, unglamorous, and the difference between a
   thing that demonstrates and a thing that operates.
4. **Sprint 18 — closing gaps after downtime.** Depends on 16 for its categories and 17 for its
   ledger.
5. **Sprint 13 — the fast layer.** What makes it *current* rather than *recorded*.
6. **Sprint 19 — control layers.** Tier one is free today; tier two waits on a licence answer that
   may be no.
7. **Sprints 12, 14 and 15** as the volume and the appetite justify them. Sprint 14's headline cost —
   one model call per observation — largely evaporates on a self-hosted model, so its priority
   depends on a deployment decision rather than on a date.

### If you want one thing first

**Sprint 10.** Sprint 11 already proved it is needed.
