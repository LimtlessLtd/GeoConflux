# ADR 035: The register of conflicts is discovered, and membership is a predicate rather than a box

- Status: Accepted
- Date: 2026-09-14
- Answers the finding in [ADR 034](034-conflict-coverage-benchmark.md), which measured that placement
  had stopped being the binding constraint and the register of what to watch had become it.
- Retires `Theatres.cs` as the register of what this system covers. It survives as the source of the
  three per-theatre caveats, which are research findings and not a list of subjects.
- Builds on [ADR 032](032-global-gazetteer-sourcing.md) and
  [ADR 033](033-tiered-gazetteer-artefact.md): a conflict's geography is worked out by resolving a
  coding project's own place names through that lexicon.

## Context

Until now a "theatre" was a hard-coded country code with an optional bounding box, and there were
three of them: Ukraine, Yemen and Tigray. They are in the repository because somebody typed them
there.

ADR 034 built a benchmark against a register this project did not write, and the result reframed the
problem. Scored against the 319 conflicts UCDP recorded events for in 2024, the lexicon can resolve
at least one place for 78% of them. **Placement is no longer what limits coverage.** What limits it
is that the system watches three conflicts and the world has hundreds, and the map's emptiness
everywhere else is therefore a statement about a configuration file rather than about the world.

Hand-authoring sixty theatres is not a plan. It is the same defect at a larger scale, and it would
decay: conflicts start, merge, split and end, and a list maintained by whoever last had time is a
list that is quietly wrong.

The material to avoid all of it was already arriving and being thrown away. Every UCDP record carries
`conflict_new_id`, `conflict_name`, `side_a` and `side_b`; the parser read all three names and dropped
them into a headline fallback. Cataloguing the world's organised violence is Uppsala's entire job,
and this repository was already parsing the field that names them.

## Decision

### The register is a coding project's, not this project's

Conflicts come from UCDP's coding: global, published criteria, human-curated, and compiled by people
with no interest in what this system can see. The 2024 extract that ADR 034 committed as benchmark
ground truth is now also the seed the running system starts from — **the same file**, moved to
`data/conflicts/ucdp-2024.tsv` and consumed by both. That is not tidiness. A benchmark scored against
a different register from the one the system watches would measure nothing.

A model may propose a conflict nothing codes yet, and it enters as a claim: stored, displayed,
labelled, and unable to take members or structure anything until corroborated. That is structurally
identical to a single-source Tier B claim, which [ADR 029](029-corroboration-gate.md) already governs,
and the reasoning is the same. A register a model wrote would be a function of the model's exposure
rather than of the world, exposure tracks volume of reporting, and so the categories most likely to be
missing are the under-reported ones. **An absent category looks exactly like peace.**

### Geography is derived from the coding, and no box is drawn

The plan specified membership over "country codes, bounds, actor keys, event types". Bounds were
built as **place identity** instead, and the substitution is deliberate.

A conflict's places are the ones the coding says its events happened at. Those names are resolved
through the gazetteer, and a conflict's countries are wherever its own places turned out to be.
Nobody draws a rectangle. The Russia–Ukraine coding resolves into Ukraine *and Russia*, because the
incursion into Kursk is in the data — a box drawn round Ukraine would have excluded it, and a box
drawn round both would have claimed the Black Sea and half of Belarus. A conflict confined to one
region of one country is held by that region's place names without anybody having to notice that it
needs narrowing, which is precisely the hand-maintenance Tigray's bounding box used to require.

**A country needs two of the conflict's own place names behind it.** Measured, the single-name
countries are collisions without exception: the Russia–Ukraine coding resolved into Turkey, China,
Romania and the Philippines on the strength of one Ukrainian place name each that is spelled like
somewhere else, and every report from those four countries would then have matched a European war on
geographic grounds. A share-of-events rule was tried first and rejected, because it discarded Russia
— which is real — while keeping nothing useful. Two independent names is the point at which
coincidence stops being the likelier explanation, and it leaves genuinely wide conflicts wide: IS
one-sided violence is coded across nine countries and still is.

### Identity assigns; geography only narrows

This is the rule the whole design turns on, and it is the same one
[ADR 005](005-location-resolution.md) draws around a model naming a place.

- **The source's own coding** settles membership outright. A UCDP record states its conflict and
  nothing this system infers improves on that.
- **Naming a party** is an identification and assigns. It is also what makes a conflict fought across
  many countries expressible at all: Israel/Iran is not a box, and an actor is the only thing that
  travels with it into Syria, Lebanon, Iraq, Yemen, the Gulf and the sea.
- **Geography assigns only where it leaves exactly one answer.** A report from a country with four
  coded conflicts is inside the geography of all four. Saying so is a fact; asserting it belongs to
  all four is arithmetic wearing the costume of analysis. Where several remain, the report is stored
  with its candidates listed and no membership, and the panel says which choice could not be made.

An observation can belong to more than one conflict, and that is correct rather than tolerated: a
Houthi strike on shipping is the war in Yemen *and* the wider confrontation. **Counts across conflicts
therefore do not sum to the total**, and anything displaying them has to say so rather than let a
reader add them up.

### Which words identify a party is measured, not decided

Party names share words. Matching on them naively assigns every report naming a government to a third
of the world's wars.

The obvious fix — a stopword list — was declined. It would encode somebody's idea of which words are
meaningful in conflict naming, be wrong in the languages nobody checked, and need maintaining as the
register grows. Counting is none of those things. Across the 2024 register, exactly two words name
parties in more than a tenth of the conflicts: **"government" in 117 of 319 and "civilians" in 103.**
The next commonest appears in 22. Those two are furniture — they say what kind of party somebody is
rather than which party — and they are dropped.

Below that cliff no threshold works, because "cartel" appears in 19 conflicts and "fulani" in 13 and
only one of those is a name. So the filter stops there, and **how many words a report shares with each
conflict** settles the rest. A report naming the Jalisco Cartel New Generation and the Sinaloa Cartel
shares one word with every cartel conflict in the register and five with the one it means. A report
that genuinely says nothing more specific than "cartel" ties across twenty and is reported as the
ambiguity it is, rather than assigned to the first eight.

### One word shared with several conflicts is a category, not a name

Running the real pipeline found what reasoning about it had not. Reports about a commercial vessel
struck off Qeshm Island were being assigned to **four Iranian conflicts at once** — including Iran's
conflict with Islamic State — on the strength of the word "Iran". UCDP writes its state parties as
"Government of Iran", so every one of those party lists contains it, and it is the name of the
country rather than of anybody fighting.

The rule that fixes it is the frequency argument above applied at match time with a threshold of one
instead of a third: **a single word shared by more than one conflict does not identify any of them.**
Two words still do, which is what keeps genuine multi-membership working — a report naming the
"Houthi movement" shares two words with each conflict that movement is a party to and belongs to
both, while a report naming only a country shares one with all of them and belongs to none of them
yet.

The two cases are structurally identical and cannot be told apart by any rule about strings, only by
how much of a name the report actually used. Where that is not enough, the report is stored with its
candidates listed and the choice is left to coded data or to a model — which is the same failure
direction geography already takes.

## Consequences

- **The register holds 319 conflicts rather than 3**, and none of them was chosen here.
- **Two thirds of the coded place names do not resolve** — 2,661 of 8,058. This is the ceiling on the
  whole text path and it is reported rather than buried: a conflict whose places this system cannot
  recognise is a conflict it cannot draw from prose, whoever reports it. It is consistent with ADR
  034's event-weighted 52%, because the names that fail are overwhelmingly the rare ones.
- **A place name matches only where the country agrees.** The register is assembled from coded place
  names and therefore holds the collisions too; without this check a report from Brazil is a
  place-level match — the strongest claim geography can make — for a war in Lebanon.
- **Two of the 319 conflicts have no identifying words at all.** `IS - Civilians` is one: "IS" is two
  characters, which is below the length any token may have, and "Civilians" is one of the two dropped
  words. Those conflicts can be reached by coding and by geography and not by name, and that is
  stated rather than hidden.
- **UCDP's party names are not reporting's party names.** The 2024 coding contains no occurrence of
  "Houthi": it codes that administration as the Government of Yemen and the internationally
  recognised side as the Presidential Leadership Council. Actor matching therefore does less work on
  wire text than it appears to, and closing that gap is the job of model assignment rather than of a
  hand-written alias table — which would be this project writing the register again through a side
  door.
- **`Theatres.cs` is no longer the register.** Its three caveats are research findings that took work
  to establish and they stay; what it stops being is the answer to "what does this system watch".
- **The extract is as current as the file.** A conflict coded since it was taken is absent rather than
  rejected, and a record naming one says so in as many words instead of being silently dropped.
- **The published page now shows very few assigned conflicts, and that is the honest figure.**
  Measured on a real credential-free run of 24 reports: **2 of 319 conflicts identified, 9 reports
  left undecided with their candidates named, 10 reaching no conflict at all.** Nothing is being
  hidden by that. The deterministic pass assigns only what a source coded, what named a party
  distinctively, or what geography left no choice about; the rest is a question for a model, and the
  offline stand-in declines to answer it. Turning on a provider is what moves those nine, and a
  credential is what moves the ten — which is the same arrangement FIRMS, ACLED and UCDP already
  ship under.

## Where this differs from the sprint definition

Two substitutions, recorded so they are decisions rather than drift.

**Bounds became place identity**, as set out above. The sprint listed "country codes, bounds, actor
keys, event types"; what was built is country codes, the conflict's own coded place names, actor
words, and event types. A rectangle is a worse instrument than the list of places the coding already
supplies, and it needs maintaining.

**Narratives are not exported into the snapshot.** They cost a model call each, most would be a
refusal because a build's evidence is thin, and a refusal computed at build time and served for days
would read as a statement about the conflict rather than about one run. They are served per conflict,
on request, from the live host.
