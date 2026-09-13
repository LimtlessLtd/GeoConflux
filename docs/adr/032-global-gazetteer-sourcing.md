# ADR 032: Global place names come from GeoNames under CC BY, beside the Wikidata theatre extract

- Status: Accepted
- Date: 2026-09-13
- Revisits [ADR 026](026-gazetteer-sourcing.md), which chose Wikidata for three theatres and named
  the condition under which that choice should be looked at again. This is that revisit, and it
  reverses the source without reversing the reasoning.
- Builds on [ADR 005](005-location-resolution.md), whose rule that only a deterministic resolver may
  produce coordinates is what makes the size of this table matter at all.

## Context

ADR 026 chose Wikidata over GeoNames on one property: Wikidata is CC0 and GeoNames is CC BY 4.0, so
the Wikidata extract carries no obligation into an artefact that is embedded in an assembly and
republished on a public dashboard. That was the right call for 2,750 places across three theatres.

Sprint 10 asks for the world, and the global coverage assessment set out why the choice has to be
made again rather than inherited. Two reasons, and the second is the one that decides it.

**The attribution obligation was avoided as a tidiness preference, not because it was burdensome.**
A `NOTICE` file at the repository root and a credit line on the page discharge CC BY 4.0 completely.
That is a small, one-off, permanent cost, and this project already carries source credits on the page
because provenance is something it argues for rather than tolerates.

**Wikidata does not scale to this, and that is measured rather than predicted.** Sprint 9 hit WDQS
timeouts and truncated JSON bodies extracting settlements for *one country*, and
`tools/gazetteer/extract.py` had to be restructured into a two-phase query against the indexed box
service — select inside a bounding box, then fetch labels in batches of 250 — to finish at all. The
comment in that script recording why the batch size is 250 is the honest artefact of the attempt:
asking for selection and labels together "times out". Extracting 250 countries that way is not a
longer version of the same job. It is thousands of box queries and hundreds of thousands of label
batches against a shared public endpoint, and it would be both unreliable and rude.

GeoNames publishes the same information as flat dumps over HTTP. Measured on 2026-09-13:

| File | Compressed | Uncompressed | What it holds |
| --- | --- | --- | --- |
| `allCountries.zip` | 401.7 MB | 1,703.7 MB | every feature, with coordinates and population |
| `alternateNamesV2.zip` | 194.7 MB | 747.2 MB | every alternate name, every script, tagged by language |
| `admin2Codes.txt` | 2.3 MB | — | the second-order administrative divisions |
| `admin1CodesASCII.txt` | 148 KB | — | the first-order administrative divisions |
| `countryInfo.txt` | 31 KB | — | per country, including which languages are spoken in it |

Two files, one pass each, no endpoint to be polite to and nothing to retry.

## Decision

### The global layer comes from GeoNames, and the obligation is discharged explicitly

`NOTICE` at the repository root credits GeoNames and states the licence. The extract's own header
repeats it, and the dashboard's source list carries it where a reader will actually see it. That is
what CC BY 4.0 asks for and it is now done rather than deferred.

The extraction is `tools/gazetteer/global.py`. It is not part of the build. ADR 026's rule that the
build is hermetic and never reaches the network is untouched and is the reason the output is
committed: a clone builds and runs offline with no credentials, exactly as before.

### The Wikidata theatre extract stays where it is

This is not a migration. The two layers answer different questions and both are kept:

- **The coarse global layer** is every first- and second-order administrative unit on earth and the
  settlement that is the seat of each. It comes from GeoNames, and it exists so that a report from a
  country nobody has tasked can be drawn at all.
- **The deep theatre layer** is settlements below that, for Ukraine, Yemen and Tigray. It comes from
  Wikidata, it is 2,750 places, and it already works.

Re-sourcing the theatre layer would be change for its own sake. It would risk the three theatres this
project can currently place well, to buy consistency of provenance that nothing depends on. The two
licences compose without difficulty: CC0 imposes no condition, CC BY imposes attribution, and the
artefact satisfies both by attributing.

The code says which layer a place came from, because ADR 026's distinction between *a coordinate a
person typed* and *a coordinate a query produced* is the reason any of this is trustworthy, and a
third category — *a coordinate a different query produced under a different licence* — is worth being
able to see.

### A place carries the spellings its own country uses, plus the ones the wire uses

This is the rule the sprint turns on, and it is where most of the size went.

GeoNames holds **1,027,796** non-historic alternate names for the 78,547 places in this tier. Taking
all of them would be absurd: it includes every Esperanto, Cebuano and Basque exonym for every district
in every country, which is weight with no reach, because no source this system reads publishes in
those languages.

Taking only English would be worse, and Sprint 9 already proved it the hard way — three Arabic items
placed only because `اليمن` and `السودان` had been added by hand. *A conflict is reported in the
language it happens in.*

So a place keeps a spelling when the spelling's language is either:

1. **spoken in that place's own country**, as GeoNames records in `countryInfo.txt` — so a township in
   Kayin State keeps its Burmese name and a Basque exonym for it is dropped; or
2. **one of the languages this system's own sources publish in** — English, French, Spanish,
   Portuguese, Russian, Arabic and Chinese. A conflict is reported in the language it happens in *and*
   in the language of whoever carried it onward, and a lexicon needs both halves.

Historic names are excluded, and that is a real choice with a visible cost: reporting does
occasionally use them, and a lexicon carrying them would place Leningrad. It would also carry every
colonial renaming and every name a place has shed, which is a large amount of new ambiguity bought
for a small amount of reach. Entries that are not names at all — postcodes, airport codes, URLs,
Wikidata identifiers, abbreviations — are excluded outright, because hunting for a three-letter
airport code in prose is actively harmful.

That rule keeps **193,445** spellings from 299,735 rows read: a little under a fifth of what the
source holds, and the fifth that some source this system reads could plausibly use.

### The extract refuses to write what it cannot check

ADR 026 records a mistake worth not repeating: a query for Tigray returned real Ethiopian places with
real coordinates, hundreds of kilometres from Tigray, and *nothing about the result set looked wrong*.
The fix there was a per-theatre bounding box. There is no equivalent here — a global extract has no
box outside it — so the checks are aimed at the failures this shape of job actually has:

- **Every row sits inside the extent of the country it claims.** The extent is computed from all five
  million features GeoNames holds for that country, not from the tier, so a tier row is checked
  against something the tier did not choose. This catches a shifted column or a bad join, both of
  which produce rows that parse.
- **Anchor spellings must survive the join.** `Київ` for Ukraine, `صنعاء` for Yemen, `ရန်ကုန်` for
  Myanmar, `አዲስ አበባ` for Ethiopia, and four more. This exists because the failure it guards against is
  silent: a join on the wrong column, or a language filter that matches nothing, produces a lexicon
  that builds, loads, resolves English perfectly, and has quietly lost every script that made the
  exercise worth doing. Asserting that two strings name one place is a judgement a person may make;
  ADR 026 permits exactly that and forbids writing a coordinate, and these assert no coordinates.
- **Separators may not occur in a name**, because one stray tab would shift every following column and
  the row would still parse.
- **Null Island is refused.** A coordinate of exactly zero in both axes is a missing value that was
  written as a number anyway.

## Consequences

The lexicon goes from 2,750 sourced places in three countries to **78,547 sourced places in 246**,
with 193,445 alternate spellings. A report naming Kayin State, Kidal or Hpa-An is drawn where it
happened rather than at a country centroid or nowhere. That is the whole point of the sprint and
everything else in the global coverage assessment was downstream of it.

That holds for every path that *names* a place — a coded dataset, an enrichment provider, a manual
submission. It deliberately does not extend to guessing a place out of raw prose, which
[ADR 033](033-tiered-gazetteer-artefact.md) measures and decides separately, because a global
administrative layer contains thousands of units named after ordinary words.

`NOTICE` now exists and must be kept accurate. That is the cost of CC BY and it is the entire cost.

**The layer is coarse, deliberately, and that has to be reported rather than implied.** It holds
administrative units and their seats. It does not hold villages, and it does not hold informal region
names: Catatumbo and Bajo Cauca — both named in the global coverage assessment as places this system
could not draw — are still not in it, because neither is an administrative unit in any national
scheme. They are exactly the case the curated editorial layer exists for, and
[ADR 033](033-tiered-gazetteer-artefact.md) is where the tiering that admits them is decided.

Coverage is uneven by country and the unevenness is large: Colombia has 2,200 places in this extract
and South Sudan has 48. That is not a defect in the extraction, it is what the source holds, and it is
the reason Sprint 10 reports the lexicon ceiling **per country** rather than per theatre. Fifty-two
countries are held with fewer than twenty places each, and a reader is entitled to know which.

The extract is a snapshot with a date, not a live mirror, for the same reason ADR 026's was: a build
that reaches the network is not one this repository will take.
