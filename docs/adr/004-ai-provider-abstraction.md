# ADR 004: Keep AI integration provider-independent

- Status: Accepted
- Context: Demo, local, OpenAI, and Azure OpenAI environments have different provider requirements.
- Decision: Define application behaviours such as event enrichment and use Microsoft.Extensions.AI where suitable; providers are infrastructure implementations selected by configuration.
- Consequences: The pipeline can use a deterministic mock provider in CI and demo mode. Model output remains untrusted input subject to schema validation.
