# ADR 004: Keep AI integration provider-independent

- Status: Accepted
- Context: Demo, local, OpenAI, and Azure OpenAI environments have different provider requirements.
- Decision: Define application behaviours such as event enrichment and use Microsoft.Extensions.AI where suitable; providers are infrastructure implementations selected by configuration.
- Consequences: The pipeline can use a deterministic mock provider in CI and demo mode. Model output remains untrusted input subject to schema validation.

## Implementation note (Sprint 3)

Built as described. `IEventEnrichmentService` is the application-level behaviour; `Microsoft.Extensions.AI`
supplies `IChatClient` as the vendor-neutral seam, and `ChatClientFactory` selects the implementation
from `Ai:Provider`. The application layer references no AI SDK at all — the enrichment contract,
prompt, and validator depend only on `System.Text.Json`.

Two decisions worth recording separately followed from this one:

- [ADR 012](012-ai-output-is-untrusted-input.md) — how model output is validated before it is adopted.
- [ADR 013](013-deterministic-provider-by-default.md) — why the default provider is an in-process stand-in.
