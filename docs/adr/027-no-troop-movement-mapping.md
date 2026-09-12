# ADR 027: This system does not map troop movements

- Status: Accepted
- Date: 2026-09-12
- Builds on [ADR 005](005-location-resolution.md), which established that a position must be derived
  by a deterministic resolver rather than asserted, and [ADR 025](025-agent-collected-osint.md), which
  wrote down a capability that was deliberately not built and why.

## Context

The brief that opened this sprint asked for "an accurate map of warzones and battles/troop
movements". That is three requests, and they have three different answers:

1. **Where is the front line?** Polygons of territorial control. Obtainable daily for Ukraine, not at
   all for Yemen or Tigray.
2. **What happened, where?** Points with a coordinate and a date. Obtainable for all three, at one to
   fourteen days of lag.
3. **Where are the units?** Order of battle and movement.

The first two are being built. The third is the subject of this decision.

Answering (1) and (2) well is a real product. Conflating them with (3) is the most likely way a
project like this stops being trustworthy, because unit positions are exactly what a reader of a
conflict map most wants and exactly what open sources cannot supply.

## Decision

**No part of this system asserts a unit position, and none will.** Not from imagery, not from social
footage, not from an analyst's marker, and not as an inference drawn by a model from any combination
of those.

The map shows control change where it is published as data, and event density where events are coded
with coordinates. A reader may draw conclusions about movement from those. The system does not draw
them on the reader's behalf, because it cannot do so from evidence.

## Why each available source falls short

This is recorded in full rather than summarised, because "we looked and there was nothing" is a claim
that should be checkable.

- **Commercial satellite imagery** (Planet, Maxar, Umbra). Tasking latency of hours to days, per-scene
  cost, and licences that forbid the republication this project would require. A movement map built
  on imagery that arrives a day late is a history of where units were, sold under a licence that does
  not permit showing it.
- **Sentinel-1 SAR.** Free, and genuinely powerful — there is published, peer-reviewed open tooling
  for Ukraine-scale destruction mapping from Sentinel-1 time series. But the revisit interval is 6 to
  12 days and the processing pipeline is well outside this repository's scope. It is a damage-mapping
  instrument, not a movement-tracking one.
- **Geolocated social footage.** Minutes old, and the fastest thing in existence. It is also
  individually unverifiable and an actively poisoned channel: both sides of a war have every reason to
  publish footage that is old, relocated, or staged. ADR 025 already anticipated this and left Tier B
  open social specified and unbuilt, because the corroboration gate that stops one uncorroborated post
  from forming an incident does not exist yet. That ordering is correct and holds here.
- **Analyst unit markers** on DeepState and ISW. Daily and expert, but inferred rather than observed,
  and not published as data. Scraping a rendered map to recover somebody else's inference, stripped of
  the reasoning that produced it, would be the worst option on this list rather than the best.

## The argument that has to be refused explicitly

The tempting version of this feature does not look like fabrication. It looks like analysis: take the
event points this system already has, add the control polygons, observe that engagements have moved
twelve kilometres west over a fortnight, and render an arrow.

The arrow is a claim about units. Nothing in the evidence is about units. Event density says where
fighting was reported, which is a function of where journalists and coders were as much as where
soldiers were — and in Tigray, where communications blackouts are recurring and dedicated ACLED
coverage ended in July 2025, it is substantially a function of who could file a report at all.

Inferring a unit position from that would be precisely the failure the rest of this project is built
to refuse — a coordinate asserted on evidence that does not support it — arriving through a door
marked "analysis" instead of through a language model. The trust boundary in ADR 005 is about what
the evidence supports, not about which component does the asserting.

## Consequences

The dashboard will not answer the question a reader most wants answered, and should say so rather
than leave the absence to be discovered. A visible statement that unit positions are not shown, and
why, is worth more than a feature that guesses: it tells the reader what kind of map this is.

This decision is about evidence, not appetite. If a source appears that publishes observed unit
positions as data, under a licence permitting republication and at a useful latency, this should be
revisited. Nothing on the list above is close to that today.

The gap is recorded here so that a future reader finds a decision rather than an oversight.
