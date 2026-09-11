# ADR 019: Aggregate analytics in SQL, sample only what SQL cannot express

- Status: Accepted
- Date: 2026-09-11

## Context

The analytics views need counts by severity, event type, region, source kind, and processing status;
an incidents-over-time series; and the inputs to the activity score — all for four time windows.

The obvious implementation is to load the window's incidents and tally them in memory. It is short,
easy to unit test against a fake, and wrong in a way that does not show up until there is data: it
reads the same table once per breakdown, and its cost grows with how busy the period was rather than
with how many classes exist. A quiet dashboard refresh and a busy one would differ by orders of
magnitude.

The opposite extreme — push everything to SQL — is blocked by how time is stored. `DateTimeOffset`
is persisted through a value converter as UTC ticks (ADR 002), so neither bucketing a timestamp into
an interval nor applying exponential decay to its age can be expressed in a query EF will translate.
Attempting it produces either a translation failure or, worse, a silent client-side evaluation that
looks like it worked.

## Decision

Split the work by what the database can actually do, and put the split behind one interface.

`IAnalyticsRepository` is a read-only port, separate from `IIncidentRepository`. That interface loads
aggregates to be mutated; this one returns counts and projections no caller can write back. Keeping
them apart means analytics code cannot acquire the ability to change state.

**Counts are `GROUP BY` queries.** Severity, event type, region, source kind, and status each return
one row per class regardless of how many incidents the window holds. They are exact and uncapped.

**The timeseries and the score use one capped projection.** `SampleIncidentsAsync` returns four
scalar columns — severity, timestamp, evidence count, confidence — newest first, capped at 5,000. Both
the bucketing and the decay are computed from that single read rather than from two.

Three details make this honest rather than merely convenient:

- **Truncation is reported, not swallowed.** The query takes `limit + 1` rows and infers truncation
  from what came back, rather than from a second `COUNT` that could disagree with it under concurrent
  writes. The flag reaches the API response and the dashboard renders a warning.
- **The window is half-open, `[from, to)`.** Consecutive windows partition time exactly, so an
  incident on a boundary belongs to one period and never to both.
- **One clock reading per report.** `GetUtcNow` is called once in `AnalyticsService` and passed to
  every query. Each query resolving "now" for itself would give the breakdowns slightly different
  periods, and a total that disagrees with the sum of its own parts is worse than no total.

Regional grouping keys on `CountryCode ?? Name`. Grouping on the country code alone would collapse
every maritime incident into one null bucket labelled by whichever name sorted first — and since most
of this system's traffic is maritime, the largest row in the regional breakdown would have been
"nowhere".

## Consequences

- Breakdowns stay exact at any volume; only the timeseries and the score are bounded, and visibly so.
- These behaviours are covered by integration tests rather than unit tests, deliberately. The
  grouping keys are enum properties stored through value converters and an owned location type — a
  fake repository would answer every one of these happily while the real query threw or fell back to
  client-side evaluation over the whole table.
- One request returns everything a window's view needs. An endpoint per chart would have the
  dashboard issue six round trips whose answers must agree and would not.
- A deployment with a native spatial extension or a different time encoding could push more of this
  into SQL without changing a caller, because the split lives behind the interface.
