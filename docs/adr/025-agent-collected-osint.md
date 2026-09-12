# ADR 025: An agent may collect and cite, and may not conclude

- Status: Accepted
- Date: 2026-09-12
- Builds on [ADR 015](015-live-provider-ingestion.md), which established the polling adapters, and on
  [ADR 005](005-location-resolution.md), whose rule about coordinates this decision both relies on
  and had to finish enforcing.

## Context

Until now every source in this system polled: a timer fired, an HTTP request went out, a parser ran.
That shape is right for an instrument. A satellite records detections continuously and answers the
same question every time it is asked, so a timer is a perfectly good way to ask.

It is the wrong shape for news, and the published deploy showed why. It reads four general-interest
English feeds, which between them answer "what did these four newsrooms put out recently" — a
question nobody asked. The feed cannot be directed at a region or a topic, it yields one paragraph
per item and never follows the link, and whether a report is visible at all depends on whether a
publisher happened to place it inside the window. Most of what arrives is irrelevant and the relevant
part arrives stripped of everything except a headline.

An OSINT collection agent answers a different question: what is being reported about this, by whom,
and where can that be read. It can be tasked, it reads the document rather than the summary of it,
and it records a retrievable citation for every item.

The difficulty is not plumbing. It is that this source is a language model, and this project's entire
credibility rests on a rule about what a language model is allowed to assert.

## Decision

### The collector may state a citation and nothing that resembles a conclusion

An agent may report that a named publisher published a document at a URL, when it says the event
occurred, when the document was retrieved, a bounded verbatim excerpt, and the place names appearing
in that text. It may not report a coordinate, an event type, a severity, a paraphrase in place of a
quotation, a merged item assembled from several reports, or anything it did not retrieve.

Everything the pipeline already does — deterministic classification, AI enrichment, schema
validation, gazetteer resolution, deduplication, correlation, severity scoring — runs over what the
agent collects, unchanged and unaware this source exists.

The prohibitions outnumber the permissions on purpose. A collector that also classifies is an
untested second classifier competing with the one this project evaluates and publishes metrics for.
A collector that supplies coordinates is the exact failure ADR 005 exists to prevent, arriving
through a new door.

### The contract's shape is the enforcement, not its validation rules

`CollectedItem` has no latitude, no longitude, no event type and no severity. A collector that was
persuaded by a page it was reading — and pages can carry instructions aimed at the agent reading
them, which is a second and separate injection surface from the one ADR 012 covers — still cannot
express a coordinate, because there is nowhere to put one.

A test asserts that absence by reflection, so adding such a field later fails with a message saying
why it must not exist. That is a stronger guarantee than any validation rule, because it cannot be
satisfied incorrectly.

### A bundle is untrusted input and gets no credit for its author

Validation runs regardless of the fact that the producer is an agent working on this repository.
Unknown schema version, unmapped property, malformed JSON, or an item count over the cap reject the
whole file. A faulty item is skipped with a stated reason while the rest of the bundle survives —
the same containment the adapters have, where one bad feed costs its own poll and nothing else.

Two details are worth recording because the obvious choice is wrong in each.

**Lengths are counted in text elements, not UTF-16 units.** A cap written for English and measured in
units cuts Arabic and Han text at a fraction of its stated limit, and can cut it mid-character. The
whole point of this source is non-English collection, so a limit that silently punishes non-English
text would defeat it.

**Control characters and bidi overrides are stripped; the zero-width non-joiner is kept.** Bidi
overrides can make a string render as something other than what it says, which matters acutely in a
field quoting right-to-left text. The ZWNJ looks like the same class of character and is not: it is
letter-affecting in Persian, and removing it turns ordinary words into misspellings. Treating "all
invisible characters" as one category would have corrupted the text this feature exists to carry.

### Provenance is three-valued because two was a lie in one direction

A bundle is real reporting, so it is not demo data. It is also not a live feed, and presenting it as
one would be the same dishonesty the demo label exists to prevent. `ObservationProvenance` is
therefore `Recorded`, `Polled`, `Collected`, and the distinction is carried to the dashboard and the
exported snapshot. `IsDemo` survives as a derived property rather than a second column, because two
columns eventually disagree.

Collected records are shown with the date they were gathered, and a bundle past its maximum age is
skipped with a warning rather than served quietly. A page that has slowly become a museum while still
describing itself as current is the failure this prevents, and it is the one hardest to notice from
outside.

### Delivery is a committed file, not a runtime call

The bundle is read from disk. That is what makes this the only source reaching real reporting with no
credential, no outbound connection, and the same result on every run — the three properties that let
the published dashboard show cited material rather than a recorded demo, and that let a reviewer
check the citations in a diff.

Calling a model with a web-search tool at poll time was considered and declined. It needs a
credential to run at all, it is not reproducible from the repository, it spends money per poll, and
it moves a model from deciding *how an observation is described* to deciding *whether one exists* — a
materially larger trust grant. It remains available as another `IEventSource` if that trade changes.

### The coordinate rule is now enforced at the door it was missing from

Writing this found that `POST /api/observations` accepted a latitude and longitude from any caller
and the resolver honoured them as `SourceProvided`: an exact position at 0.95 confidence, from an
anonymous stranger, in the configuration this repository ships. ADR 005 has always said that must not
happen, and it was reachable by default.

Source-provided resolution is now reserved for the two observation kinds that are measurements rather
than accounts — a satellite geolocating a pixel, and a curated dataset publishing a coded location.
Everything else names a place and the gazetteer places it, or stays unplaced.

## Consequences

- The published dashboard can show cited reporting in languages the polled feeds do not carry, with
  no credential and no outbound call from the application.
- Adding an OSINT agent would have been worthless without the gazetteer work that shipped alongside
  it. The enrichment prompt asks for the place as it is named in the text, which for an Arabic source
  produces an Arabic name, and a Latin-only lexicon resolved none of them. The first real bundle
  proves the point: three of its seven items are Arabic, and all three placed only because
  `اليمن` and `السودان` now resolve.
- A collected item enters as `News` or `Manual` and never as a measurement kind, so it can never take
  the source-provided path regardless of what a future bundle contains.
- `contentHash` is over the excerpt, not over the retrieved page. Hashing volatile HTML would produce
  a value that never matches on re-fetch and therefore proves nothing; hashing the excerpt proves the
  quotation was not edited after collection, and the lint checks it on every build.
- Re-fetch verification is deliberately not part of the build. A build that fails whenever a publisher
  reorganises its site is a build that gets ignored.
- Every committed bundle is linted by the same parser that reads it at runtime, inside the existing
  test suite rather than as a separate CI step. One gate that cannot be forgotten beats two that can.
- Expiry is not a build failure, which is a deliberate departure from how this was first specified.
  A bundle going stale is a runtime condition the source already warns about, and failing the build
  for it would break unrelated work a fortnight after the last collection run, for a reason no commit
  caused.
- Nothing here reads a platform that gates access. X answers an unauthenticated request with HTTP 402
  and Weibo redirects to an authentication wall; both are recorded as gaps rather than worked around,
  and holding an account that misrepresents an automated collector as a person is not an option this
  project will take. A paid credential would move such a source into the ordinary authenticated
  adapter pattern ACLED already uses.
- Tier B open social — Telegram public channel previews and Bluesky author feeds, both verified
  publicly readable — is specified but not built. It needs the corroboration gate first, because a
  single uncorroborated post must not be able to form an incident, and nothing yet stops it.
