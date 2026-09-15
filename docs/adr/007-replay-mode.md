# ADR 007: Provide deterministic replay without credentials

- Status: **Superseded by [ADR 040](040-real-reporting-only.md)** (2026-09-15). The replay source and
  the demo seeder are deleted: the pipeline now reads ten live feeds and the committed collection
  bundles, so the premise below — that there would otherwise be nothing to show — no longer holds,
  and a system that reports what sources said should not contain a component that invents records.
- Status when accepted: Accepted
- Context: A portfolio demonstration cannot rely on provider uptime or private credentials.
- Decision: Add a recorded, sanitised replay source in Sprint 2 and keep its data clearly labelled as demo data.
- Consequences: Tests and demos are reproducible without fabricating live-source activity.
