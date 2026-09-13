# ADR 029: One post is evidence a post exists; two are evidence of an event

- Status: Accepted
- Date: 2026-09-13
- Completes [ADR 025](025-agent-collected-osint.md), which specified Tier B open social and recorded
  that it could not be built until this rule existed.
- Qualifies [ADR 006](006-event-correlation.md), whose correlator answers a related but weaker
  question, and [ADR 022](022-open-write-path-and-content-policy.md), whose open endpoint turns out
  to be governed by this rule too.

## Context

The plan's Tier B is Telegram public channels, Bluesky author feeds and Mastodon public timelines.
These are where conflict reporting breaks first and, for large parts of the world, where it breaks
only: the assessment is blunt that there is no readable equivalent across much of Africa and South
Asia because the dominant platform is WhatsApp and it has no public surface at all.

They are also the sources least able to support what this system does with a source. A wire item
comes from a named organisation with an editorial process, a correction policy, and a reputation it
is unwilling to spend. A Telegram post comes from a handle. It may be a first-hand account minutes
old and better than anything a wire will publish that day; it may equally be an anonymous claim,
footage recycled from a different war, or deliberate deception produced by a party to the conflict.
Contested information space is the normal operating condition for these channels, not an edge case.

Excluding them is not the answer, because excluding them means excluding the fastest and often the
only reporting from inside an event. ADR 025 recorded the alternative and then declined to build it,
for a reason worth repeating exactly: *a single uncorroborated post must not be able to form an
incident, and nothing yet stops it.*

## Decision

### A claim is stored, placed, shown, and denied an incident

A user-generated observation that matches no existing incident is persisted in full — classified,
gazetteer-placed, announced to connected clients, drawn on the map — with the status
`Uncorroborated` and no incident.

What is withheld is not the record. It is the assertion. An incident is this system saying *something
happened*; an observation is it saying *a source said this*. One post supports the second sentence
and not the first, and the gate is the place where those two stop being the same act.

Three consequences follow from holding rather than hiding, and each of them was the reason for it:

- **A hidden claim is a claim nobody can corroborate.** It must be visible for a second source to be
  recognised as agreeing with it.
- **Hiding claims would conceal how much of the picture rests on unsupported posts**, which is the
  exact bias the coverage measurement exists to expose.
- **`Uncorroborated` is a resting state, not a verdict.** It says "not yet" and never "no".

### Corroboration is stricter than correlation, and answers with a boolean

The correlator already asks whether two reports describe the same event. It is tuned so that a missed
merge beats a wrong one, and it answers with a confidence between 0 and 1.

The gate asks a different question — may an event be asserted at all — where a wrong answer
manufactures an incident out of two posts. It is therefore stricter in three specific ways:

- **Agreement on a country is not enough.** The correlator accepts it at a reduced ceiling, which is
  right for deciding whether two reports concern one event once something already believes the event
  happened. "Both somewhere in Yemen" is not evidence that anything happened, and on a busy day it
  would let two unrelated posts assert an event between them.
- **The radius is a third of the correlation radius** — 25 km against 75. The correlation radius is
  drawn around a metropolitan area. This one is drawn around a town and its outskirts, because two
  channels putting a strike 60 km apart are more likely describing two strikes.
- **It returns a boolean.** "May this exist" is not a question a confidence answers, and producing a
  score here would invite somebody to tune a threshold until the answer came out convenient.

### Wording is never required, and that is the whole point of reading open social

The gate corroborates on strong positional evidence *alone*: same category, same 25 km, inside the
window. Only where the positional evidence is weaker — two records merely naming the same place —
does it also demand that the text or the actors agree.

Requiring shared vocabulary throughout would have been the obvious design and it would have been
wrong in a way that is hard to see. A lexical similarity measure scores two accounts of one strike
near zero when one is in Ukrainian and the other in English. The rule would have quietly restricted
corroboration to claims written in the same language — which is precisely the limitation this whole
tier exists to escape. Enrichment produces an English summary that would partly paper over this, but
only when a model is configured, and the deterministic configuration this repository ships has none.
A safety rule whose correctness depends on an optional component is not a safety rule.

### Independence is a channel, and it carries its platform

Two claims corroborate only when they come from different named identities. A single account posting
twice is one source posting twice, and that is the cheapest possible attack on a gate like this.

The identity is `platform/channel` rather than the handle alone, because a handle is only unique
within its platform: `telegram/reuters` is not `mastodon/reuters`, and an identity that dropped the
platform would let either stand in for the other.

### The open write path is governed by this too

Writing this found the same hole ADR 025 found one field over. `POST /api/observations` takes no
credential, is reachable by anyone on the network, and was opening incidents. An anonymous submission
is the definitional user-generated claim: no editorial process, and no identity either.

It is therefore attributed as `Unattributed` — user-generated, with no platform and no channel. The
missing identity is load-bearing rather than incidental: it means two anonymous submissions can never
corroborate each other, because "two strangers agreed" is one unverifiable assertion repeated. Such a
claim can still *join* an incident that exists on other grounds. It can never bring one into being.

This changes documented behaviour, and it should. The alternative was a system whose strictest rule
had a door marked "anyone on the network" that bypassed it.

### A held claim is released when a second source arrives

The sequence this has to handle is the ordinary one, not the exotic one: social breaks first and the
wire follows. Whenever an incident is created or joined, held claims that the incident now accounts
for are linked to it in the same save, and re-announced so a reader watching the page sees the label
change without reloading.

Without that sweep the gate would be a mechanism for losing the fastest reporting rather than a
mechanism for qualifying it — a post held at 09:00 would still be held at 17:00 after three outlets
had reported the same strike.

The sweep runs on a join as well as a creation, because an incident that has just gained a location
or an actor may account for a claim it did not a moment ago. It is an indexed query that returns
nothing in the common case, and it is bounded: `MaxHeldClaimsPerSweep` caps the work and the gate
says so in its rationale rather than trimming silently.

## Consequences

- **Tier B can now be collected.** The bundle schema has carried `kind`, `platform` and `channel`
  since ADR 025 and enforced the pairing; what was missing was anything that treated the distinction
  as meaning something. It now does.
- **`CorrelationGate` was renamed `CorrelationLock`.** It is a per-category mutex serialising
  correlate-then-commit and shares nothing with this decision but a syllable. The rename happened
  because reading the name alone was enough to mistake a mutex for the trust rule — which is exactly
  what happened while assessing what this sprint still had to build.
- **Held and released are counted separately**, because the gate is silent by design. It produces no
  error and no gap in the feed, so a rule that started holding everything would otherwise look
  exactly like a quiet week. Held against released is also the honest measure of whether Tier B is
  paying for itself.
- **Recycled content is still not detected, and this does not fix it.** Two channels reposting one
  claim verbatim will corroborate each other. A near-identical-text rule was considered and declined:
  two independent posts saying "explosion at the port" are genuinely near-identical and genuinely two
  witnesses, so the rule would reject true positives to catch a case it cannot reliably identify. The
  plan records this as a known limitation and it remains one.
- **A claim placed only at country level can never be corroborated by another claim.** It is not
  stranded — it is released the moment an incident it matches exists, by the ordinary correlation
  path, which weighs country-level agreement properly against everything else it knows. But two such
  claims will sit held indefinitely, and that is the intended trade.
- **The committed bundles are unaffected.** All seven items in the published bundle are Tier A
  documents, so the live page shows exactly what it showed before. The gate changes what *will*
  happen when Tier B collection starts, which is the right order to build it in.
