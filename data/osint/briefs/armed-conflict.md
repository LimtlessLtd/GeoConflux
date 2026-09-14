# Standing collection brief: armed conflict

- **id:** `armed-conflict`
- **revision:** 1
- **since:** 2026-09-14

The second standing brief, and the first that is not about water. The maritime brief beside it reads
the same channels for disruption around shipping chokepoints; this one reads them for the fighting
itself. Both exist because a single brief covering everything would be a brief covering nothing — a
term list wide enough for both would collect a commodity price story and a battlefield report under
the same heading and make the bundle misrepresent what was looked for.

The executable half of this document is `armed-conflict.run.json` beside it: the channels, the query
terms, and the caps, in the form the collection tool reads. It is committed for the same reason this
file is. A reader can see exactly what the dashboard was and was not looking at.

Tasking is configuration, not conversation. Every bundle records the brief id and revision it ran
under, so two runs a month apart are comparable.

## Scope

Organised armed violence on land, and its immediate humanitarian consequences.

**In scope:** armed clashes between organised parties, shelling and airstrikes, offensives and
counteroffensives, sieges, ambushes, the capture or loss of populated places, ceasefires and their
breakdown, civilian casualties attributed to fighting, and displacement caused by any of it.

**Out of scope:** individual criminal violence, policing operations with no armed-group party,
defence procurement and budgets, military exercises not framed as a response, commemoration, and
commentary about any of the above.

The boundary against the maritime brief is the water. An attack on a vessel belongs there; an attack
on a port town belongs here; an event that is plainly both will be collected by both, which is
visible in the bundles rather than resolved by a rule.

## Recency window

Published within the previous 7 days. An older report qualifies only when it is the first account of
something inside the window.

## Source tiers in scope

**Tier A and Tier B**, in any language.

Every channel in the run file was verified on 2026-09-13 as serving public content with no credential
and no JavaScript, for the maritime brief. They are reused rather than re-chosen, because
public-readability is the property that took work to establish and because two briefs reading the
same channels make their bundles directly comparable.

A Tier B item is cited to its channel, is visibly a claim rather than a report, and cannot form an
incident alone — [ADR 029](../../../docs/adr/029-corroboration-gate.md).

## Languages

Every concept in the term list is carried in English, Arabic, Ukrainian and Russian, French, Spanish
and Persian. This is the brief's most important property and the reason it is not simply a longer
English list: a conflict is reported in the language it happens in, and searching in English finds
what English publishes.

The obvious English words are deliberately absent. *Attack* matches a heart attack, *killed* matches
a road accident, and *strike* matches industrial action. A term list that over-collects is worse than
a short one, because the bundle then claims to have looked for something it did not.

## Source diversity

| Cap | Value | Why |
| --- | --- | --- |
| Per channel | 5 | A prolific channel must not dominate the picture by posting more. Higher than the maritime brief's three because the scope is wider: taking three headlines from a wire service publishing forty relevant ones and calling it a picture is the failure this cap is supposed to prevent, not cause. |
| Per platform | 15 | One busy platform must not stand in for the world. This is the cap that binds hardest in practice. |
| Run total | 60 | A bundle a person can actually review in a diff. |

## What a run of this brief does not establish

The same limits the coverage panel states everywhere else, and they bite harder here than on the
maritime brief. These channels are international wires and one state agency; between them they cover
a handful of the conflicts the register holds, and their attention follows the same reporting
gradient that makes under-reported conflicts under-reported. A quiet result from this brief is a
statement about what these ten channels published this week, and about nothing else.
