# ADR 034: Coverage is scored against a register of conflicts this project did not write

- Status: Accepted
- Date: 2026-09-14
- Depends on [ADR 032](032-global-gazetteer-sourcing.md) and
  [ADR 033](033-tiered-gazetteer-artefact.md), which built the lexicon this measures.
- Supersedes nothing, and is the reason two things in ADR 033 changed within a day of being written.

## Context

This project reports its own coverage, at length and in several places. The Coverage tab states what
was placed per theatre and per country, the source list says what each channel gave, and the caveats
say what each theatre's numbers cannot establish. All of it is true, and all of it shares a defect
that no amount of care inside the system could fix: **it is scored against a list this repository
wrote**.

`Theatres.cs` names Ukraine, Yemen and Tigray. They are there because somebody chose them. The
gazetteer was extracted for exactly those three. The coverage panel reports against exactly those
three. A reader is therefore told, in careful and honest detail, how well this system covers the
things it decided to cover — and nothing whatsoever about the rest of the world.

That is not a small gap. Reporting coverage against a self-selected denominator is the specific form
of dishonesty this project spends most of its effort avoiding elsewhere: it makes a statement about a
configuration file read as a statement about the world.

The question that exposes it is simple. *If an obscure war were happening — Murle against Nuer in
Jonglei, Balanda against Azande in Western Equatoria — would this system be able to see it?* Nothing
in the codebase could answer that, because every measurement it took was relative to its own choices.

## Decision

### The denominator comes from UCDP, and the benchmark asks what fraction of it is reachable

The Uppsala Conflict Data Program codes organised violence worldwide into named conflicts with named
parties, on published criteria, and is the standard the field uses. Its Georeferenced Event Dataset
recorded **28,816 events across 319 distinct conflicts in 2024**, each with the place it happened at.

That register is the denominator. `tools/conflicts/extract.py` derives it once and commits it to `data/conflicts/`, and
`Geopolitics.ConflictBenchmark` asks, per conflict, how many of its places this system could resolve.

**It is obtainable without a credential.** The UCDP API requires an access token; the flat dataset
downloads do not. That is the same arrangement GeoNames has, and it is the reason this benchmark
could be built now rather than filed behind a request nobody has made.

### It measures the text path, deliberately

A coded dataset supplies its own coordinates and needs no gazetteer at all. Measuring UCDP's
placement against UCDP's own coordinates would score 100% and mean nothing.

So the benchmark asks the question that matters for the conflicts nobody is watching: *if a report
named this place in a sentence, could it be placed?* ACLED and UCDP both require credentials and both
lag by months to years. An obscure war reaches this system, if it reaches it at all, as text.

### The floor it asserts is a collapse detector, not a standard

The test fails below 72% of conflicts reachable and 62% of the least-reported ones — a few points
under what was measured when it was written. That guards against a lexicon that stops loading or a
merge that starts discarding names. **Passing it does not mean the coverage is good**, and the
results file is written so that nobody can read it that way.

## Consequences

### What it found immediately

The benchmark earned its place before it was finished, which is the argument for it.

**A missing tier.** ADR 033 had defined the coarse layer as administrative units *and their seats* —
reasonable, and wrong. GeoNames codes Acapulco, population 658,609, as a plain populated place, so it
was absent; the administrative unit beside it is named "Acapulco de Juárez" and carries none of the
spellings anybody writes. The benchmark could place 45% of recorded events. Adding populated places
above 5,000 took it to 52%, and Africa from 25% to 33%.

**A disambiguation rule that collapsed under scale.** Adding the world's towns pushed contested names
from 8,673 to 14,581, taking Homs, Morelia, Jabaliyah and New York with them, because the rule that
recognises "a town and the district named after it are one place" required *every* candidate to be
mutually nested. One Ukrainian hamlet called Niu-York was enough to stop New York State, New York
County and New York City from being recognised as one place. Collapsing first and comparing second
fixed it, and the distance proxy behind it was replaced with GeoNames' own containment codes.

Neither defect was visible to the existing tests, which all passed throughout. Both were visible
immediately to a benchmark with an external denominator.

### What it says about the system as it stands

| | |
| --- | ---: |
| Conflicts UCDP recorded in 2024 | 319 |
| Theatres this system is configured to watch | 3 |
| Conflicts with at least one resolvable place | 249 (78%) |
| Recorded events at a resolvable place | 15,083 of 28,816 (52%) |
| Least-reported conflicts with no resolvable place at all | 70 of 225 |

**Placement is no longer the binding constraint; attention is.** The lexicon reaches 78% of the
world's recorded conflicts. The register of what to *watch* is three entries long and hand-written,
so an empty map outside those three is a statement about a source list rather than about the world.
Closing that is Sprint 16 — conflicts discovered from dataset coding rather than authored — and this
benchmark is the thing that will say whether it worked.

**Coverage is sharply uneven by region, and not in the system's favour.** The Americas place 87% of
recorded events; Africa places 33% and Asia 37%. Most of the long tail is in Africa and Asia. The
places this system can least see are the ones it was least likely to be told about anyway.

**What remains unplaceable is mostly genuine.** The residue is village-level coding — Ukrainian
front-line hamlets below any global population floor, Gaza neighbourhoods and refugee camps, and
transliterations GeoNames does not carry (`Jabaliyah` is not in it under that spelling). A
gazetteer-shaped answer runs out around here; the fix for the rest is per-theatre depth, bought
deliberately, which is what the tiering exists to allow.

### What it does not measure

**Reach, not knowledge.** A resolvable place name means a report naming it *could* be drawn. It does
not mean anything was reported, that this deployment reads any source covering it, or that the
placement is precise — most of the world outside the tasked theatres resolves to a district or
province centroid.

**A register that lags.** UCDP v25.1 ends at 2024. A conflict that began or resumed since is absent
from the denominator entirely — including Tigray's resumption, which this repository's own caveat
dates to January 2026. That is not a flaw in the benchmark so much as the strongest argument for the
text path it measures: the datasets find out late, and a system that waits for them is a system that
reports the year before last.
