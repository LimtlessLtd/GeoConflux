# ADR 006: Model observations separately from incidents

- Status: Accepted
- Context: Multiple sources can describe one underlying event, and deleting duplicate evidence loses provenance.
- Decision: Retain observations and associate them with incidents through a correlation assessment based on deterministic and semantic signals.
- Consequences: The incident aggregate tracks linked observation count while provenance stays available for later display and review.
