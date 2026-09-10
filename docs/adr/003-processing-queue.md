# ADR 003: Use a bounded Channel for initial asynchronous processing

- Status: Accepted
- Context: Ingestion must not be coupled to UI delivery, but a durable external broker is not justified for the initial single-node deployment.
- Decision: Introduce an application queue abstraction backed by a bounded `System.Threading.Channels` implementation in Sprint 2.
- Consequences: Backpressure, cancellation, controlled concurrency, and shutdown can be tested in-process. The abstraction leaves room for a durable broker if requirements later warrant one.
