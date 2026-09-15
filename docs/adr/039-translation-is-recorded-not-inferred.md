# ADR 039: Translation is recorded, the source text is kept, and the reader chooses which to read

- Status: Accepted
- Date: 2026-09-15
- Extends [ADR 012](012-ai-output-is-untrusted-input.md) from coordinates to translations: a model's
  claim to have translated is untrusted input like any other, and is checked rather than believed.
- Depends on [ADR 013](013-deterministic-provider-by-default.md), which is the reason the published
  page shows untranslated text at all, and whose honesty about that is preserved here rather than
  worked around.
- Changes the enrichment contract to schema v2 and the prompt to v2.

## Context

The dashboard reads ten feeds in five languages. Six of them were added on 2026-09-14 for a measured
reason: the snapshot before them was 95 English observations out of 99, and a conflict is reported in
the language it happens in.

Reading them turned out not to be the same as understanding them.

### What the page actually did

Three separate gaps combined into one defect.

**The headline was never translated by anything.** Normalisation copies the source's own title
verbatim, and enrichment never touched it — the contract had no field for it. The feed list displays
that title and almost nothing else, so the single line a reader scans was, for every Arabic and
French and Spanish feed, in Arabic and French and Spanish.

**The source text was stored and unreachable.** `RawObservation.Content` holds the original body and
always has. It was not on `ObservationResponse`, so it never reached the API, the snapshot, or the
page. Where enrichment did produce an English summary it *overwrote* the only text a reader had, and
the original it replaced could not be fetched back to check it against.

**The page claimed a translation whenever the language was not English.** The chip read `ar → en`,
and it was rendered on the sole condition that `detectedLanguage !== 'en'`. That condition establishes
nothing about translation:

- The language tag is normally lifted straight off the feed at intake, before any model has seen the
  text. `RawObservation` sets `DetectedLanguage = DeclaredLanguage` in its constructor.
- The provider that ships by default is `DeterministicMockChatClient`, which states in its own
  summary text that it did not translate — `[Mock enrichment — not translated]` — and which is what
  the published deploy runs on, because no credential is configured for it.

So the common case on the live page was an Arabic headline, a summary saying in English that nothing
had been translated, and a chip asserting that Arabic had been rendered into English. The one piece
of the interface that spoke about language was the piece that was wrong.

## Decision

### Translation is a recorded fact, not an inference

`RawObservation` gains a `TranslationState` — `NotTranslated`, `AlreadyEnglish`, `MachineTranslated` —
set from what the provider says it did, never derived from a language tag. `MachineTranslated`
additionally records `TranslationMethod`, so the English on screen can be attributed to a named model
in the same way a classification already is.

The enrichment contract carries a `translated` boolean for the provider to answer, rather than having
the pipeline guess from the presence of a non-English tag. Only the provider knows.

The claim is checked, not taken. The validator honours `translated: true` only when English text
actually arrived with it, and the domain refuses a `MachineTranslated` state that names no translator
or carries no English. A record asserting a translation it does not hold would put the page back
exactly where it started.

### The source text is evidence and is never overwritten

`Title` and `Content` remain the source's own words for the life of the record. The English goes to
`TranslatedTitle` and `TranslatedSummary` beside them.

This is the same rule the rest of the pipeline already follows — a declared category outranks an
inferred one, a stated coordinate outranks a resolved one — applied to text. A translated claim that
cannot be compared with what was published is not checkable, and this repository's whole position is
that a conclusion which cannot be checked should not be presented as one.

`ObservationResponse` therefore carries both sides, and the snapshot exporter publishes both. The
original is excerpted at 4,000 characters, which is the bound normalisation already applied to a
summary built from the same text, so the worst-case payload is no larger than the one that shipped
before the original was exposed at all.

### A low-confidence classification does not discard a translation

The pipeline already keeps the factual extractions from a model that reported low confidence in its
*classification*, on the grounds that being unsure whether a report is piracy or a maritime incident
says nothing about whether the text is Arabic. The same reasoning applies to the English it wrote, so
the translation is kept on that path too. The classification is still refused; that is what the low
confidence justified.

### The reader chooses, and is told what they are looking at

The feed carries a two-state control: **English** and **Source text**. English is the default and the
choice is remembered.

Neither mode hides anything. Source text shows the original headline and body, laid out with
`dir="auto"` so right-to-left scripts read correctly. English shows the translation where one exists
and the source's own words where one does not — labelled as such, never disguised.

The chip now makes three distinct statements instead of one indiscriminate one:

| State | Chip | Claim |
| --- | --- | --- |
| `MachineTranslated` | `ar → en` | A named model produced the English on screen. |
| `NotTranslated`, foreign | `ar · not translated` | Nothing translated this; the text is the source's own. |
| `AlreadyEnglish`, or English | *(none)* | Nothing worth saying. |

An unknown language raises no warning. "We do not know what this is" and "you cannot read this" are
different facts, and warning about the first would bury the second.

The shortfall is also stated once, in prose, above the feed: *N of M reports are shown in the source
language: nothing translated them.* A page that reads five languages and translates none of them
should say so where it cannot be missed.

## Consequences

**The published page will, for now, mostly say it did not translate.** That is the truthful report of
what `Ai:Provider = Mock` does, and it is an improvement on the previous state, where the same page
made the opposite claim about the same text. Configuring a real provider turns the same machinery
into real translations with no further change; until then a reader can at least read the source text,
see the language, and know exactly what they are and are not being given.

**The mock is not asked to translate.** It composes summaries from facts it can establish — the
script, the keyword category, the gazetteer match — and inventing English for text it cannot read
would be fabrication indistinguishable on the dashboard from a real model's work. It answers
`translated: false` and always will. ADR 013's honesty is the reason this defect was visible at all.

**Schema and prompt both move to v2.** A stored v1 inference has neither new field and reads back as
untranslated, which is true of every observation processed before this change. The version is what
makes that legible rather than silent.

**Two more nullable text columns per observation.** Bounded at 400 and 1,200 characters, and null for
every record nothing translated — which is most of them today.

**The correlation path is untouched.** `Summary` remains the text the deduplicator and the
corroboration gate compare, and the English rendering is deliberately kept out of it. Changing what
correlation reads is a separate decision with its own evidence, and translating the corpus first
would have quietly changed which reports match which.
