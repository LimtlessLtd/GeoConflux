# ADR 011: Ship a deterministic classifier behind the interface AI will later implement

- Status: Accepted
- Date: 2026-09-10

## Context

Sprint 2 delivers the processing pipeline; AI enrichment arrives in Sprint 3. An observation still
needs a category and a severity in order to be correlated and displayed, so something had to assign
them in the meantime.

Two options were available: leave everything `Other`/`Unknown` until Sprint 3, or classify
deterministically now.

## Decision

Introduce `IEventClassifier` and implement it with `KeywordEventClassifier`, a keyword and severity
matcher with no external dependency. Source-declared categories always take precedence over it;
the classifier only fills genuine gaps.

Its confidence is capped at 0.65 and it reports its method as `keyword`.

When AI enrichment arrives, it implements the same interface and this classifier remains as the
fallback for when a provider is unavailable or returns output that fails schema validation.

## Consequences

- The pipeline is genuinely useful and fully testable with no credentials, which is the point of the
  offline demo.
- Correlation can be developed and tested against real categories rather than placeholders.
- The confidence cap matters: keyword matching is a heuristic, and presenting it at a high score
  would misrepresent it to anyone reading the dashboard.
- The score is currently computed but not persisted or returned by the API, so the UI cannot show
  it. Until it is threaded through `RawObservation` and the response contracts, no part of the
  product may claim that confidence is displayed. The dashboard therefore states plainly that the
  classifier is keyword-based rather than implying a scored judgement.
- Sprint 3 gains a fallback path for free, so an AI outage degrades classification quality instead
  of stopping ingestion.
- The classifier is English-language and keyword-bound. It will misclassify unusual phrasing. That is
  acceptable for a stopgap whose confidence output already says not to trust it much.

## Outcome (Sprint 3)

Both predictions held, and one gap identified here has been closed.

`KeywordEventClassifier` is now the fallback rather than the primary classifier. Enrichment runs
first; when the provider is unavailable, times out, or returns output that fails validation or falls
below `Enrichment:MinimumAcceptedConfidence`, this classification stands and the failure is recorded
as an `AiInference` row rather than absorbed silently.

The confidence score is no longer computed and discarded. `RawObservation.ClassificationConfidence`
and `ClassificationMethod` are persisted, carried through the response contracts, and displayed
beside every category on the dashboard — so the caveat this ADR recorded ("no part of the product may
claim that confidence is displayed") no longer applies, because it now is.

The classifier's English-language limitation is no longer a footnote but a measured result. The
evaluation harness reports it: on the Arabic, Russian, and Chinese fixtures the offline baseline has
nothing to match on, reports low confidence, and is correctly not adopted. See
[tests/data/ai-evaluation/RESULTS.md](../../tests/data/ai-evaluation/RESULTS.md).

One defect was found by an integration test during this work: the keyword `pirac` does not match the
word "pirates", which is the commonest term for it in reporting. `pirate` was added.
