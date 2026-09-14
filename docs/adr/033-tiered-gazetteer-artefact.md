# ADR 033: The gazetteer is tiered, and the global tier is one line per place

- Status: Accepted
- Date: 2026-09-13
- Settles the artefact-size question raised in
  [the global coverage assessment](../global-coverage-plan.md), §2.2, which asked for this to be
  decided in an ADR before the data was written — the same rule that governed Sprint 9.
- Depends on [ADR 032](032-global-gazetteer-sourcing.md), which chose the source.

## Context

The largest file in this repository before this sprint was the 895 KB Wikidata theatre extract. The
assessment estimated a global extract at **30–80 MB uncompressed** and said plainly that this "does
not go in a repository comfortably". It offered three options:

1. **Commit a compressed binary artefact**, decompressed at startup. Keeps the build hermetic. Costs
   startup time and memory, "and makes the artefact unreviewable in a diff — which is a genuine loss,
   because reviewability in a diff is one of this project's stated properties."
2. **Download at first run.** Breaks the hermetic build. Rejected once already.
3. **Tier it.** A global-but-coarse layer committed always, deep layers committed only for theatres
   under active tasking. "This is the one I would take, and it is the only one where the artefact
   stays reviewable."

## Decision

### Tier it, as the assessment recommended

Three layers, and the code says which is which, because they have different provenance and different
rules and that difference is what makes the whole table trustworthy:

| Layer | What it holds | Where it comes from | Size |
| --- | --- | --- | --- |
| **Curated core** | Chokepoints, seas, country centroids, contested and informal naming | Hand-written in `Gazetteer.cs`, argued for in comments | ~250 entries |
| **Global coarse** | Every first- and second-order administrative unit on earth, and every populated place above 5,000 inhabitants | GeoNames, CC BY 4.0 | 127,377 places, 289,307 spellings |
| **Theatre deep** | Settlements below district level, for theatres under active tasking | Wikidata, CC0 | 2,750 places, 9,179 spellings |

**The coarse layer originally held administrative units and their seats, and that was wrong.** The
correction is recorded here rather than quietly applied because the mistake is instructive: an
administrative *seat* is a role in a national scheme, not a synonym for "somewhere people live", and
the two diverge exactly where reporting is thickest. GeoNames codes Acapulco — population 658,609 —
as a plain populated place, while the administrative unit around it is a separate record named
"Acapulco de Juárez" carrying none of the spellings anybody writes. The city was therefore absent and
`Acapulco` resolved to nothing, as did Morelia, Khan Yunis and a great many others.

Nothing in the reasoning found that. [The conflict-coverage benchmark](034-conflict-coverage-benchmark.md)
found it, by asking how much of the world's recorded violence this lexicon could place and answering
45%. Adding populated places above a floor took that to 52%, and Africa from 25% to 33%.

The floor is 5,000 because that is where the curve flattens, measured rather than chosen:

| Population floor | Places | Artefact | Conflicts reachable | Events placed |
| ---: | ---: | ---: | ---: | ---: |
| seats only | 78,547 | 6.1 MB | 73% | 45% |
| 15,000 | 100,698 | 7.7 MB | 76% | 50% |
| **5,000** | **127,377** | **10.2 MB** | **77%** | **52%** |
| 1,000 | 195,696 | 13.0 MB | 77% | 54% |

The last two percentage points cost 41% more artefact, and buy one additional conflict.

The tiering is honest about what it is. The global layer places a report to a district or a town; it
does not place one to a village, because it does not hold villages. Adding a theatre is what buys that depth,
and it is a deliberate act with a cost, which is exactly how this project already thinks about
theatres. It also makes the coverage panel's statement concrete: *we hold 105 places for Myanmar* is
a limit a reader can act on, in a way that *coverage is uneven* is not.

### The global tier is one line per place, not JSON and not a binary

The assessment framed option 1 as a trade between size and reviewability. Measured, that trade turned
out to be mostly an artefact of the encoding rather than of the data. The same 78,547 places:

| Encoding | Size | Lines | Reviewable in a diff |
| --- | --- | --- | --- |
| JSON, `indent=1` — the existing theatre extract's format | 19.2 MB | ~940,000 | Barely: one place is twelve lines |
| JSON, no whitespace | 14.2 MB | 1 | No |
| **One tab-separated line per place** | **6.1 MB** | **78,547** | **Yes: one place is one line** |
| That, gzipped | 2.2 MB | — | No |

*(Measured at the 78,547 places the tier held when the decision was taken. The committed file is now
10.2 MB, having gained the world's towns and two administrative-code columns; the ratio between the
encodings is what the decision turned on and it is unchanged.)*

So the choice that keeps the artefact smallest *also* keeps it most reviewable, and the compression
option buys 3.9 MB in exchange for every property this repository says it values. A changed place is
one changed line in a diff. The file is greppable. Nothing is decompressed at startup and nothing is
fetched during a build.

The format is the header, then one line per place:

```text
# name	lat	lon	country	precision	rank	population	admin1	admin2	spellings separated by |
Kayin State	17.2	97.75	MM	R	1	1574079	13		Karen State|Kayin|ولاية كايين|ကရင်ပြည်နယ်|克倫邦
```

The two administrative codes say which units a place sits inside, and they are there to replace a
guess. The merge has to recognise that Homs the city, Homs the district and Homs the governorate are
one place described at three scales, and it used to decide that by asking whether their centroids
were within 1.5° of each other — a tolerance [ADR 026](026-gazetteer-sourcing.md) calibrated against
Yemeni governorates. Large units defeat it: Homs Governorate reaches into the eastern desert, and New
York State's centroid is 250 km from New York City. Recording the containment makes it a fact rather
than an inference.

The header is comment lines carrying provenance, the extraction date and the counts, so the file
states what it is without a reader having to find the script that wrote it.

Separators are checked at extraction rather than assumed: a name containing a tab would shift every
following column, and the row would still parse.

### The coarse tier resolves names; it does not hunt for them in prose

The tiers differ in what they are *for*, and that turned out to decide more than which file they live
in. `Gazetteer` has two entry points, and ADR 026 put the difference between them exactly: a caller
passing a name to `TryResolve` has asserted that it is a place name, while the scanner finding that
name inside a sentence has guessed.

**Only a tier somebody chose is hunted for in prose.** The curated core and the theatre layers are;
the coarse layer is not.

This was measured rather than argued. Scanning every committed corpus of realistic prose in this
repository — the evaluation fixtures, the severity corpus and the replay observations — with the
coarse layer in the scan, **38 of its names fired, and almost every one was an ordinary English word
that is also a real administrative unit somewhere**:

| Fired on | Is actually |
| --- | --- |
| "Exchange of detainees completed" | Exchange, a populated place |
| "quiet night along the contact line" | Along, a town in Arunachal Pradesh |
| "scheduled maritime boundary talks" | Maritime, a region of Togo |
| "through the city centre on Sunday" | Centre, a region of Cameroon |
| "dispersed by early evening" | Early County, Georgia |
| "police reported no arrests" | Police, a populated place |
| "damages buildings in a border village" | Village, a populated place |

with `Northern`, `Southern`, `Central`, `Major`, `Union`, `University`, `Legal`, `Frontier`, `Burns`,
`North` and a run of American counties — Power, Gates, Ferry, Cross, Sharp — behaving the same way.

Two narrower rules were tried first and both failed. A length floor lets `Frontier` and `University`
through. Dropping alternate spellings and keeping only each place's preferred name moved 38 to 24.
The collision is intrinsic: holding every administrative unit on earth means holding thousands that
are named after ordinary words, and no rule about *the name* separates them, because there is nothing
wrong with the names.

Restricting the scan to the chosen tiers took it from 38 firings to 7, and all seven are correct
placements. The evaluation harness's location metric is **1.00 / 0.92 / 0.96**, unchanged from before
the global layer existed.

Almost nothing is lost, which is why this is the trade rather than a retreat. Everything that
*names* a location arrives through `TryResolve` — a coded dataset, an enrichment provider, a
submission — and that path holds all 78,547 places and every spelling of them. What the coarse tier
no longer does is let the offline provider guess a district out of raw prose, and the table above is
what that guess was worth.

The consequence is worth stating plainly rather than burying: **an English-language wire report that
names only a district in a country with no theatre layer will be stored, classified and left
unplaced.** Giving it a coordinate would mean guessing, and this is the project that says an unplaced
report is visibly unplaced while a confidently misplaced one is not.

### The theatre tier stays as JSON

It is 895 KB and 2,750 rows, the verbosity costs nothing at that size, and its structure is richer —
it carries a Wikidata item id and a theatre name the global rows have no use for. Converting it would
mean re-running the Wikidata extraction, which risks the three theatres this project can currently
place well in exchange for consistency that nothing depends on.

The rule is that the format follows the size. A 2,750-row file is fine as JSON. A 78,547-row file is
not, and would have been the largest and least reviewable thing in the repository.

## Consequences

### What it costs at runtime

Measured after the layer was built, because the assessment's worry about option 1 was startup and
memory and it would have been dishonest to decide without checking:

| | Before Sprint 10 | After |
| --- | ---: | ---: |
| Places in the lexicon | 2,750 in 3 countries | 81,297 in 246 |
| Spellings resolvable | ~11,900 | ~205,000 |
| Spellings hunted for in prose | ~11,900 | 11,018 |
| Automaton states | — | 83,831 |
| Cost of one prose scan | 0.732 ms | **0.024 ms** |
| Managed heap after load | — | 61.7 MB |

The scan got **thirty times faster** while the lexicon grew thirtyfold, which is the automaton of
[ADR 026's](026-gazetteer-sourcing.md) "the lexicon needs an index rather than a linear scan" doing
exactly what it was adopted for. `GazetteerScaleTests` pins the property rather than the number:
scanning against 200,000 spellings costs 0.98× what scanning against 200 costs, so the figure above
is a constant rather than a budget being spent down.

Memory is the real cost, and 61.7 MB is acceptable for a host that already runs a database and an ML
model. It is dominated by the resolvable spellings, not by the automaton — which is a direct
consequence of the previous decision, since the scan indexes only the tiers somebody chose.

### What it costs in the repository

The repository grows by 6.1 MB, which is real and is the price of being able to draw a report from
anywhere. It is committed, so a clone still builds and runs offline with no credentials, and the
hermetic build rule from [ADR 026](026-gazetteer-sourcing.md) is untouched.

The file is embedded as an assembly resource, as the theatre extract and the severity corpus already
are, so a published single-file build carries it.

**The parse now has to be defended, which JSON did for free.** A hand-rolled line format has failure
modes a deserialiser would have rejected — a short row, a malformed number, a duplicated header — so
the loader treats the file as untrusted input in the same way [ADR 012](012-ai-output-is-untrusted-input.md)
treats a model's output, and the parse is tested against each of those shapes rather than assumed.

Two encodings now coexist, which is a wart. It is a smaller one than either converting a working
extract for tidiness or committing the largest file in the repository in the least reviewable form
available.
