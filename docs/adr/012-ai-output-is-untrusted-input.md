# ADR 012: Treat model output as untrusted input with one repair attempt

- Status: Accepted
- Date: 2026-09-11

## Context

Sprint 3 introduced AI enrichment. A model produces the summary, category, severity, language,
entities, and place name for an observation, and all of it is displayed to a reader.

A language model is a remote service that returns text. It can return malformed JSON, a category
that does not exist, a confidence of 7, a hundred entities, control characters, or a field nobody
asked for. It can also be influenced by the observation text itself, which arrives from public feeds
and from an open submission endpoint and must therefore be assumed hostile.

## Decision

Model output crosses a validation boundary before any of it is adopted.

1. The request is constrained where the provider supports it. A JSON schema is sent with every call;
   providers with constrained decoding rarely return anything invalid.
2. The response is validated regardless, so the guarantee does not depend on the provider having
   that feature. `EnrichmentPayloadValidator` checks the schema version, maps enums against an
   explicit wire vocabulary, bounds every length and count, rejects non-finite and out-of-range
   confidence, strips control characters, and refuses unmapped properties.
3. All problems are collected rather than the first, and handed back to the model as a single
   repair turn alongside its rejected answer.
4. Exactly one repair attempt is allowed. A model that cannot satisfy the schema when given the
   precise errors is unlikely to on a third try, and each retry costs latency and spend.
5. What is persisted is a re-serialisation of the *validated projection*, never the provider's raw
   text. Fields outside the contract cannot reach the database.
6. Below `Enrichment:MinimumAcceptedConfidence` the *classification* is not applied — the
   deterministic one stands — but the factual extractions are. Confidence describes certainty in the
   classification, so a model unsure whether a report is piracy or a maritime incident may still be
   entirely right that the text is Arabic and names Bab-el-Mandeb. Language, place name, and
   entities are kept; category, severity, and summary are not. Nothing adopted this way can place
   the observation: a name still has to survive the deterministic resolver.

Two rules follow from the contract's shape rather than from validation code. The schema has no
latitude or longitude field, so a model cannot supply a coordinate even if asked to
([ADR 005](005-location-resolution.md)). And the payload is wrapped in explicit delimiters with a
restatement that it is data — mitigation for prompt injection, not a guarantee, which is exactly why
validation runs regardless of what the model was persuaded to do.

## Consequences

- A provider that is down, slow, or wrong costs classification quality and never costs evidence.
  The observation is stored either way.
- Every attempt writes an `AiInference` row — provider, model, prompt version, schema version,
  outcome, confidence, attempts, latency — so degraded classification is visible rather than silent.
- Rejecting an unknown category rather than mapping it to `Other` means model drift surfaces as a
  validation failure instead of hiding behind a plausible default.
- Storing only the validated projection means no chain-of-thought is retained, which is a
  requirement rather than an optimisation.
- The repair turn costs a second call on malformed output. That is the price of not discarding an
  otherwise-usable response over a formatting mistake, and it is capped at one.
