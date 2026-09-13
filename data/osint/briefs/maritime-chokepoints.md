# Standing collection brief: maritime chokepoints

- **id:** `maritime-chokepoints`
- **revision:** 2
- **since:** 2026-09-12 (revision 2: 2026-09-13)

Revision 2 opens Tier B open social, which revision 1 excluded because nothing yet stopped a single
post forming an incident. The corroboration gate now does — see
[ADR 029](../../../docs/adr/029-corroboration-gate.md) — so the exclusion has been lifted and the
source diversity section below has gained the caps that go with it.

The executable half of this document is `maritime-chokepoints.run.json` beside it: the channels, the
query terms, and the caps, in the form the collection tool reads. That file is committed for the same
reason this one is. A reader can see exactly what the dashboard was and was not looking at.

Tasking is configuration, not conversation. This file states what a collection run looks for, so two
runs a month apart are comparable and so a reader can see what the dashboard was and was not looking
at. Every bundle records the brief id and revision it ran under.

## Scope

Disruption to, and armed activity around, the maritime chokepoints through which a disproportionate
share of world trade passes.

**In scope:** Bab-el-Mandeb and the southern Red Sea, the Strait of Hormuz and the Persian Gulf, the
Suez Canal, the Turkish Straits and the Black Sea, the Strait of Malacca, the Taiwan Strait, the Kerch
Strait, the Panama Canal, and the Gulf of Guinea. Attacks on vessels and crews, port seizures and
closures, mining and drone activity, naval deployments framed as responses, insurance and rerouting
decisions, and displacement caused by fighting adjacent to these waters.

**Out of scope:** commodity price movement with no stated security cause, routine naval exercises,
company financial reporting, and commentary about any of the above.

## Recency window

Published within the previous 7 days. An older report qualifies only when it is the first account of
something inside the window.

## Source tiers in scope

**Tier A and Tier B**, in any language.

Tier A is open documents. Tier B is open social — Telegram public channel previews, Bluesky author
feeds, and Mastodon public timelines — and it entered scope at revision 2, when the corroboration
gate made it safe to read. A Tier B item is cited to its channel, is visibly a claim rather than a
report, and cannot form an incident without an independent second source.

Tier C is out of scope and stays out. X answers an unauthenticated profile request with HTTP 402 and
its `robots.txt` disallows the path; Weibo redirects to visitor authentication; WhatsApp has no
public surface at all. These are recorded as gaps rather than worked around, and holding an account
that misrepresents an automated collector as a person is not an option this project will take.

Bluesky is collected by named account rather than by search, because `app.bsky.feed.searchPosts`
answers 403. That is a limit on what may be read, not a preference, and it is why this brief names
channels.

## Query terms, by language

Searching in English finds what English publishes. Each concept is carried in every language this
brief covers, with the transliteration variants that differ by who is writing.

| Concept | Terms |
| --- | --- |
| Strait / chokepoint | strait, chokepoint · مضيق · пролив · 海峡 / 海峽 · تنگه |
| Vessel attacked | vessel attacked, ship struck, missile, drone · هجوم على سفينة · атака на судно · 船只遇袭 |
| Shipping disrupted | rerouting, suspended transits, war risk premium · تعليق الملاحة · приостановка судоходства · 航运中断 |
| Port | port seized, port closed · ميناء · порт · 港口 |
| Bab-el-Mandeb | Bab el-Mandeb, Bab al-Mandab · باب المندب · Баб-эль-Мандеб |
| Hormuz | Strait of Hormuz · مضيق هرمز · تنگه هرمز · Ормузский пролив · 霍尔木兹海峡 |
| Taiwan Strait | Taiwan Strait · 台湾海峡 / 臺灣海峽 |
| Black Sea / Kerch | Black Sea, Kerch Strait · Чорне море · Чёрное море · Керченський протік |

## Source diversity

At most **3 items per publisher** in one run, and at most **2 items per publisher per event**. A
prolific outlet must not be able to dominate the picture simply by publishing more.

Revision 2 adds the same limit one level up, because Tier B fails differently. At most **3 items per
channel** and at most **6 items per platform** in one run. A single busy Telegram channel could
otherwise supply a whole run on its own, and one platform standing in for the world is the bias that
looks exactly like working global coverage — the map has pins on it, nothing errored, and the picture
is one platform's.

The per-platform cap binds in practice and is meant to. The first run under this revision dropped an
Al Jazeera item for it, which is recorded here rather than in a log nobody reads.

Where a publisher runs editions in more than one language, collect both and keep them as separate
items citing their own URLs. The divergence between them is itself the observation, and the pair does
not count against the per-publisher cap because it is one act of collection, not two sources.

## Exclusions

Opinion, analysis, editorial, aggregator reposts, and anything behind a paywall where only the teaser
is retrievable.

A repost or a boost is excluded specifically. It is one claim travelling, not a second witness, and
collecting it as a channel's own item would let an account corroborate itself by amplification —
defeating the corroboration gate at the collection step rather than the pipeline one.

A source that refuses an automated request is a refusal, not an obstacle: it is recorded as
unreachable and the run moves on. A source that answers and carries nothing readable is a third
thing again — an account that exists and publishes nothing — and a run reports all three separately,
because collapsing them into an empty space is the false claim of coverage this project is built not
to make.

## Volume

At most **40 items per run**.

## What a run may not do

The collector states who published what, when, where to read it, a bounded verbatim excerpt, and the
place names appearing in that text. It does not state coordinates, an event type, a severity, a
translation in place of a quotation, or anything it did not retrieve. Those limits are enforced by the
bundle schema rather than by this document, because a page being read can argue with a document and
cannot argue with a schema.
