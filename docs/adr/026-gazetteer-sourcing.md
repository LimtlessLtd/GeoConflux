# ADR 026: Theatre place names come from a committed Wikidata extract, beside a curated core

- Status: Accepted
- Date: 2026-09-12
- Builds on [ADR 005](005-location-resolution.md), whose rule that only a deterministic resolver may
  produce coordinates is what makes the size of this table matter so much, and on
  [ADR 025](025-agent-collected-osint.md), which found the same problem for Arabic and fixed it for a
  handful of names by hand.

## Context

`ObservationKindRules.MayDeclareCoordinates` permits only `Satellite` and `ExternalEvent` to state
their own coordinates. Everything else — RSS, collected bundles, manual submission — is placed
exactly as precisely as `Gazetteer.cs` can place it, and no better. That rule is correct and is not
in question here. Its consequence is.

The lexicon held 205 entries: 148 whole countries, 16 seas and straits, and **41 settlement-precision
places for the entire world**. Against the three theatres this sprint is aimed at, that meant Kyiv,
Kharkiv, Odesa and Donetsk for Ukraine; Sana'a and Aden for Yemen; and for Tigray, nothing at all —
`ET` resolves to a country centroid roughly 600 km from Mekelle. A Tigray report could be ingested,
deduplicated, enriched, scored and correlated, and still have nowhere to go on the map.

So the table has to grow by an order of magnitude, and at a few hundred entries it stops being
something to hand-write in a C# array. That raises three questions the sprint plan asked to be
settled before any data was written: where the data comes from, what licence it carries, and what it
costs in artefact size and build time.

There is a fourth question underneath those, and it is the one that actually decides this. **Nobody
working on this repository can write a coordinate.** Hand-authoring several hundred latitudes and
longitudes means producing them from recollection, and a coordinate produced from recollection is
indistinguishable in the file from one that is correct. That is precisely the failure ADR 005 exists
to prevent, arriving through a door marked "data entry" instead of "language model". Whatever is
chosen here has to make coordinates *sourced* rather than *asserted*.

## Decision

### Theatre place names come from Wikidata, extracted once and committed

Wikidata publishes all of its content under **CC0**. That is the decisive property, and it is worth
being explicit about why, because the two obvious alternatives are both worse on exactly this point:

- **GeoNames** is CC BY 4.0. Usable, but it attaches an attribution obligation to a derived artefact
  that is then embedded in an assembly and republished on a public dashboard.
- **OpenStreetMap** is ODbL. Share-alike on a derived *database* is a real question for a committed
  extract, and answering it correctly requires more care than a place-name table is worth.
- **Wikidata** is CC0: no attribution condition, no share-alike, no obligation that has to travel
  with the artefact.

Wikidata is credited in this ADR and in the extract's own header because that is courteous and
because provenance is useful, not because a licence compels it.

The extract is taken **once**, by a committed script, and the result is committed as a data file.
Downloading at build time would make the build non-hermetic and would break the requirement that a
clone runs offline with no credentials.

### The curated core stays, and stays hand-written

The existing array is not replaced. It holds maritime chokepoints, seas, country centroids, and the
alias judgements that make ordinary reporting language resolve — `Bab el Mandeb` for `Bab-el-Mandeb`,
`Kiev` for `Kyiv`, `Arabian Gulf` and `Persian Gulf` for the same water, `West Bank` for Jerusalem.
Those are editorial decisions about contested and informal naming, and a general-purpose database
will not make them. They are few, they are argued for in comments, and they belong in code.

So the lexicon has two layers with different provenance, and the code says which is which: a small
curated core that a person decided, and a large sourced extract that a query produced.

### A name that denotes two places is dropped, not guessed at

The curated table throws on an alias collision, and should keep doing so: it is small, a collision
there is a mistake, and failing at first touch is how it gets fixed.

The extract cannot behave that way. Real place names collide constantly and there is no editorial
answer to pick from. But "collide" turned out to cover three different situations, and treating them
alike would have thrown away the most-reported place in a theatre. So there are three rules and a
refusal:

1. **One place, entered twice.** The source holds duplicate items for the same town whose coordinates
   differ in the fourth decimal. Within about five kilometres, they are one place and the first is
   kept. Dropping a real place over a rounding difference would be absurd.
2. **Nested administrative units.** Marib is a governorate, a district and a city; Taiz is a city and
   a governorate. These are not rival places, they are one place described at three scales, so the
   containing unit is chosen and its `Region` precision already tells the reader it is an area rather
   than a position. This matters more than it sounds: read as a collision, "Marib" would have been
   dropped from a Yemen gazetteer, and Marib is what the fighting there is mostly about. In practice
   the governorate centroid sits three kilometres from the city that shares its name.
3. **A city among villages.** Where one candidate is at least ten times larger than every other and
   above a floor, the bare name is taken to mean it.

**Anything else is dropped, and the number dropped is recorded** — 536 names at the time of writing.
Resolving one to whichever candidate the query happened to return first would put a pin in the wrong
place with full confidence, which is worse than leaving the report unplaced. An unplaced report is
visibly unplaced; a confidently misplaced one is not.

Rule 3's threshold is worth stating plainly because it has a visible cost. Kostiantynivka in Donetsk
Oblast is a front-line town of 72,888 named constantly in reporting, and it shares its name with four
other Ukrainian places — the largest of which has 12,081 inhabitants. Six to one is not a city among
villages, it is two towns, so the name is dropped and reports naming it go unplaced. Lowering the bar
until that specific town passed would be fitting the rule to the answer, and the failure it would buy
is invisible: reports about a town of twelve thousand silently drawn two hundred kilometres away.

The curated layer wins over the extract where the two name the same place, because the curated entry
was chosen deliberately.

### The editorial layer may add a spelling, and may never add a place

The extract is only as good as the alternate labels the source happens to hold, and for small places
it often holds none. Zalambessa, on the Eritrean border, arrived with exactly one Latin spelling while
reporting uses several — which is the instability this sprint was warned about for Tigray.

So a small curated table may attach additional spellings to places **that already exist in the
extract**. Asserting that two spellings name one place is a judgement a person can make and defend.
Writing a coordinate is not, and stays out of reach: an entry whose target is not in the extract
throws rather than creating a place.

### Size is bounded by what reporting names, not by what exists

Taking every settlement is not an option and the numbers say why. Wikidata holds 11,716 settlements
with coordinates in Yemen and 19,903 in eastern and southern Ukraine. So each theatre gets a rule
matched to the precision its reporting actually supports:

| Theatre | Rule | Entries |
|---|---|---|
| Ukraine | Settlements in the eastern and southern war zone with population ≥ 1,000, plus any settlement in the country with population ≥ 50,000 | 2,105 |
| Yemen | Every governorate and district, plus settlements with population ≥ 20,000 | 482 |
| Tigray | Every settlement and woreda with a coordinate | 163 |

That is 2,750 places and 9,179 alternate spellings, 895 KB of committed JSON.

Yemen is districts first by deliberate choice rather than by convenience: reporting there, and the
Yemen Data Project's own record of the air war, states locations as governorate → district → area and
no more precisely. A district gazetteer is what that reporting can actually use. Wikidata holds 336
Yemeni districts, which corroborates the assessment's independent estimate of "~330".

Tigray needed its woredas as well as its towns. A settlement-only query missed them, and reporting
from Tigray names them constantly — the January 2026 clashes at Mai Degusha were reported as
Tselemti, which is a woreda and not a town.

### A short sourced name is resolvable but is not hunted for in prose

The two entry points ask different questions, and the bulk extract made the difference matter. A
caller passing "Sad" to `TryResolve` has asserted that it is a place name. The scanner finding "sad"
inside a sentence has guessed.

That guess is safe for a couple of hundred curated entries, whose two-letter aliases were each chosen
deliberately and already have the word-boundary handling that makes `US` usable. It is not safe for
eleven thousand sourced spellings. The extract contains real Ukrainian villages named **Sad, Rama,
Gora, Aura, Bile and Ewa**, aliases including **Luck** (for Lutsk) and **Mare** (for Marianivka), and
Roman numerals — `III` and `IV` are recorded alternate names for two Yemeni governorates.

This was not a theoretical worry. Adding the extract dropped the evaluation suite's location
extraction from **1.00 / 0.92 / 0.96** to **0.91 / 0.83 / 0.87**, because "a stroke of luck" now named
a Ukrainian city. The thresholds are loose enough that every test still passed; the committed metrics
file is what caught it.

So a sourced spelling of four characters or fewer is searched for in prose only when it is the
place's own preferred name *and* the place has at least twenty thousand inhabitants. Length alone
would not do — Kyiv, Lviv, Sumy, Uman, Aden, Ibb, Axum and Adwa are all four characters or fewer and
all are named by those names constantly. Exact lookup is untouched: every spelling still resolves
when a caller asks about it directly. The metrics returned to 1.00 / 0.92 / 0.96.

### The linear scan stays, because it was measured rather than assumed

`Gazetteer` has two entry points with very different costs. `TryResolve` is a frozen-dictionary
lookup and does not care how large the table is; it is the path every real placement takes — the
resolver, and both coded-event adapters. `FindFirstMention` scans every searchable spelling against
the text, so it is linear in the size of the lexicon, and it runs on every observation the offline
enrichment provider sees, which under the default configuration of ADR 013 is every observation the
published pipeline processes.

That looked like the binding constraint, so it was measured before being designed around rather than
after. At the original ~400 search terms a scan cost **0.024 ms**. At the ~11,900 the expansion
produces it costs **0.525 ms** — a few hundred milliseconds across an entire published run, and about
thirty times a cost that was negligible to begin with. A multi-pattern automaton would be faster and
is not worth its own complexity at this size.

The number to watch is the ratio rather than the absolute: the scan is linear, so a lexicon ten times
this size would cost five milliseconds per observation and the algorithm would need replacing. That
is the point at which this decision should be revisited, and it is why the figure is pinned by a test
rather than left to be rediscovered.

`GazetteerScaleTests` pins both figures with deliberately loose bounds, so the day someone adds a
zero to this table the cost becomes visible rather than mysterious.

## Consequences

Text reports naming Pokrovsk, Marib or Mekelle now resolve to those places rather than to a country
centroid, in the scripts the sources actually use. That unlocks every text path at once — RSS,
collected bundles, manual submission — which is the point: no number of new adapters substitutes for
it.

The artefact grows by 895 KB of committed JSON, embedded as a resource so it travels with the
assembly. Build time is unchanged, because nothing is fetched during a build.

The extract is a snapshot with a date, not a live mirror. Places are renamed and administrative
divisions are redrawn, and this table will drift from Wikidata until the script is run again. That is
the correct trade for a hermetic build, and the alternative — a build that reaches the network — is
not one this repository will take.

**Coverage will be uneven, and it has to be reported rather than implied.** The rules above admit
front-line towns above a population floor and exclude the villages below it, so a report naming a
hamlet of four hundred people will still go unplaced. That is a stated limit, not a silent one, and
the theatre coverage reporting this sprint adds is where it gets stated.

## What was checked, and one thing that was got wrong

The first extraction query asked for settlements in Wikidata item `Q207521`, which reads plausibly as
Tigray. It returned Addis Alem and Wolaita Sodo — places in Oromia and southern Ethiopia, hundreds of
kilometres outside Tigray. `Q207521` is the **Ethiopian Empire**, a state that ended in 1974, and the
transitive administrative lookup had walked up through it into most of the country.

Nothing about that result set looked wrong. The names were real Ethiopian places, the coordinates
were real, the count was plausible, and had it been committed it would have put Tigray reports several
hundred kilometres from Tigray with a settlement-precision label on them.

That is why the extractor validates every row against a per-theatre bounding box and refuses to emit
a file if any row falls outside it. A sourced coordinate is only better than an invented one if
something actually checks that the right source was asked.
