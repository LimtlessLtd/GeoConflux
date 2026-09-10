# ADR 008: Publish SignalR messages after persistence

- Status: Accepted
- Context: Browser connectivity is not a reliable processing boundary.
- Decision: Processing persists state before publishing a SignalR notification; the pipeline must work with zero connected clients.
- Consequences: Realtime UI is a projection of committed state and can recover by querying the API.
