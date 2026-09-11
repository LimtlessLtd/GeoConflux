# ADR 013: Ship a deterministic stand-in as the default AI provider

- Status: Accepted
- Date: 2026-09-11

## Context

The specification requires the application to run, demo, and pass CI with no external credentials,
while also using a real provider-independent AI integration.

Those pull in opposite directions. Enrichment is now a stage every observation passes through, so
something has to answer when no provider is configured.

Three options were considered: skip enrichment entirely when unconfigured; require a local Ollama
daemon; or implement an in-process `IChatClient` that answers deterministically.

## Decision

Implement `DeterministicMockChatClient` and make it the default `Ai:Provider`.

It is a test double, not a small language model, and it is built to be impossible to mistake for
one. It composes output only from what it can establish deterministically — the script the text is
written in, the category the keyword classifier assigns, the place the gazetteer recognises — and it
returns JSON that must satisfy the same validator a real provider's response does. Its output is
labelled `ai:Mock/deterministic-stub` in the database, in the API, and on the dashboard.

Critically, it does not invent a translation. For text in a script it cannot read, the summary says
so plainly and reports the structured facts instead.

`Ai:Provider` switches to `Ollama`, `OpenAI`, or `AzureOpenAI` with no code change. Ollama is reached
through its OpenAI-compatible endpoint rather than a second SDK; Azure OpenAI uses its own client
because it authenticates and routes differently, and claiming support without it would be a claim
rather than a feature.

## Consequences

- `dotnet run` and `dotnet test` work with no credentials, no network, and no local model.
- The offline path is the *real* path: the same prompt, schema, validator, repair loop, telemetry,
  and audit record. Only the responder differs. A bug in enrichment plumbing fails CI.
- The published static snapshot is built by a genuine pipeline run against this stand-in, so what it
  shows was produced by the code rather than authored by hand.
- The evaluation harness therefore measures the offline baseline by default. Those figures are
  reproducible and are what CI enforces; they say nothing about any language model, and the emitted
  report states that in its header.
- Because the stand-in wraps a keyword classifier, its confidence is capped and it declines to
  classify text it cannot read. On the Arabic, Russian, and Chinese fixtures it reports low
  confidence and the pipeline keeps the deterministic classification — which is the honest offline
  outcome and also a clear demonstration of the gap a real model fills.
- A reader could still misread the offline demo as AI output if every label were removed. That is
  why the labelling is in the persisted method string rather than only in the UI.
