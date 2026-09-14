# ADR 037: Control is assessed from evidence that travels with the assessment, or it is not asserted

- Status: Accepted
- Date: 2026-09-14
- **Supersedes** the position recorded in section 4 of
  [the global coverage assessment](../global-coverage-plan.md) that an assessed control-of-terrain
  map is out of reach, and narrows what that position was actually right about. See *The correction*
  below.
- **Does not touch** [ADR 027](027-no-troop-movement-mapping.md). No unit position is asserted here,
  and no movement is inferred. That decision stands unchanged and this one is built inside it.
- Extends [ADR 029](029-corroboration-gate.md) from claims about events to claims about control, and
  [ADR 012](012-ai-output-is-untrusted-input.md) from coordinates to conclusions.
- Depends on [ADR 035](035-conflicts-as-first-class-objects.md) for actors and
  [ADR 033](033-tiered-gazetteer-artefact.md) for places.

## Context

The recorded position was that an assessed control-of-terrain map is an analyst product and out of
reach: ISW's map is made by people, daily, from geolocated footage, and the assessment *is* the
product rather than a by-product. A model writes the sentence in a second; what it cannot produce is
the institution standing behind it.

### The correction

That argument is sound and it establishes less than it was used for. It establishes that **this
system cannot produce ISW's product** — a polygon whose authority is the organisation that drew it.
It does not establish that control cannot be assessed from evidence at all.

The two differ in where the authority sits. An analyst's polygon is authoritative because of who drew
it, and it is not falsifiable by a reader: the reasoning behind it is not published, so there is
nothing to check. An assessment that **carries its evidence, names its method, and states its own
age** is authoritative only to the extent its evidence supports it — and is therefore checkable,
which the analyst product is not.

So this is not a worse version of ISW. It is a different artefact with a different claim attached, and
the claim it makes is one this repository is already built to support everywhere else: *here is what
we think, here is exactly what it rests on, and here is how thin that is.*

### What was already being thrown away

The same shape as Sprint 16, and found the same way. ACLED's `sub_event_type` includes
`Government regains territory` and `Non-state actor overtakes territory` — a human coder asserting
that control of a place changed hands, with a date, a coordinate and a named actor. That is the
strongest possible input to a control assessment, and it is already arriving in payloads this
repository parses.

`AcledResponseParser` reads the field, uses it to build a headline, and maps it to
`EventType.MilitaryMovement`. The specific territorial assertion is discarded at that line.

## Decision

### An assessment is a four-part claim, and all four parts are published

> As of **T**, actor **A** is assessed to hold place **P**, on evidence **E**, by method **M**.

`E` is a list of observation identifiers, not a count. If a reader cannot click through from the
assessment to the records behind it, it is not an assessment — it is a guess wearing one's clothes,
and this repository has refused that artefact consistently enough that it would be strange to start
now.

### Only positive evidence asserts. Silence never does

This is the rule the whole design turns on, and it is ADR 027's own argument applied to a new
conclusion. Event density says where fighting was *reported*, which is a function of where
journalists and coders were as much as where soldiers were.

It follows that:

- **Fighting at a place is not a claim to hold it.** An actor being coded as active somewhere is
  evidence about that place; it is not control, and it must never be rendered as it.
- **Absence of reported fighting is not evidence of stable control.** It is the coverage panel's
  argument exactly: a quiet district means nobody reported, not that nothing happened. Silence
  therefore produces **staleness** — an assessment ageing — and never a continued assertion.

### Three tiers of evidence, in descending defensibility

| Tier | Evidence | Why it is weighted where it is |
| --- | --- | --- |
| **A** | Coded territorial change (ACLED sub-event types) | A named organisation's coder already made this assessment, with a date and a coordinate. Relaying it with attribution is aggregation, not assessment. |
| **B** | Corroborated capture or withdrawal claims | A claim that a place has fallen, supported by independent sources. [ADR 029](029-corroboration-gate.md) already governs this exact shape. |
| **C** | Administrative-control signals in prose | Appointments, documents issued, utilities restored, checkpoints run. Weak individually, real in aggregate, and reported in prose rather than in coded fields. |

Tier A alone can assert on its own authority. Tier B requires corroboration, exactly as a claim about
an event does. Tier C never asserts alone and only ever supports.

### The model classifies; it does not supply

Tier C is where a language model earns its place, and the boundary is the one this repository has
drawn three times already. The model is asked **what kind of control signal this text carries**, from
an enumerated set, about a place that has already been resolved by the gazetteer and an actor that is
already in the register.

It does not supply a place. It does not supply an actor. It does not supply a date. It does not
decide that control changed — it labels the evidence, and the deterministic layer decides. Every call
is recorded in `AiInference` with its prompt version and confidence, as enrichment and conflict
assignment already are.

### Confidence decays with age, and the decay is published

An assessment resting on evidence from two days ago and one resting on evidence from six weeks ago
are different claims and must not render alike. Every assessment carries the age of its newest
supporting record, and past a staleness horizon it reads as *last asserted N days ago* rather than
as *held*.

ISW re-assesses daily. A pipeline that cannot must say so rather than let an old assessment keep the
appearance of a current one.

### Contested is a first-class answer, and disagreement is never averaged

Where evidence supports two actors at one place, the output is **contested**, with both sides' evidence
shown and dated. Averaging them, or picking the larger pile, would manufacture a consensus that does
not exist — [ADR 029](029-corroboration-gate.md)'s argument applied to geometry.

### Below an evidence floor, it declines

The narrative floor from [ADR 035](035-conflicts-as-first-class-objects.md), applied to control. A
place with one stale report does not get an assessment with a low confidence number beside it,
because a reader takes in the claim and not the decimal. It gets a refusal: *two reports in six
weeks; too little to assess*.

### Assessment is per place, never per polygon

**Nothing interpolates between assessed places.** A polygon drawn through two assessed points asserts
control of everything between them, which nothing observed and nobody reported. That is precisely the
fabrication this repository refuses everywhere else, arriving through the door marked cartography.

Each assessed place renders at its own precision — the precision the gazetteer and the source
actually support — and the map never fills the space between them. This is how *never render a single
merged front line* becomes a property of the data model rather than a rule somebody has to remember.

A genuine polygon may still be shown later, from a named source that drew it, dated and attributed.
That is tier 2 of Sprint 19 and it is a different feature with a different claim.

### The word "assessed" is earned

The previous position was that the dashboard must never use *assessed* about anything this system
generated. That is now too strong in one direction and too weak in another. The rule becomes:

**The word may be used only where the method, the evidence and the age travel with it.** A bare
"assessed" on a coloured region is forbidden as firmly as it was before.

## Consequences

- **Most of the map will carry no assessment, and that is the correct output.** Control is assertable
  only where positive evidence exists. On a credential-free clone that is almost nowhere, and the
  panel will say so in the same terms the coverage panel already does. A map that filled itself in
  would be the failure.
- **Turning on ACLED changes this more than any other feature in the repository.** Tier A is the only
  tier that asserts on its own authority, and it arrives entirely through a credential that is free
  for research and has not been requested.
- **A reader can disprove an assessment.** That is the product argument, and it is the one thing
  ISW's map structurally cannot offer. It also means wrong assessments will be visibly wrong, which
  is a cost worth paying and should be expected rather than treated as a defect.
- **ADR 027 is untouched and its hardest paragraph still governs.** Nothing here asserts a unit
  position, and the arrow that paragraph refuses — twelve kilometres west over a fortnight, therefore
  movement — remains refused. Control changing hands at a place, coded by somebody accountable, is a
  different statement from a unit having moved.
- **This adds a conclusion the system publishes, which is a genuinely larger step than anything
  before it.** Every previous feature published what sources said, or what a deterministic rule
  derived, with the reasoning visible. This publishes an assessment. The four disciplines above are
  what keep it inside the same standard, and if any one of them is dropped the feature should be
  withdrawn rather than qualified.
- **Staleness will be the most common failure mode**, not error. Places assessed once and never
  revisited will accumulate, and the horizon is what stops them reading as current. That horizon is a
  configuration value and choosing it needs the same measure-before-deciding treatment retention got.
