# ADR 017: Spatial queries use an indexed bounding box and exact great-circle distance, not SpatiaLite

- Status: Accepted
- Date: 2026-09-11

## Context

The specification asks for real spatial querying — incidents within X km of a point, incidents near
a maritime chokepoint, nearby observations — and is explicit that spatial technology must not be
used merely as a storage format for latitude and longitude. It is equally explicit that the system
must handle gracefully the possibility that spatial native dependencies are unavailable.

SpatiaLite is the obvious candidate, so it was evaluated rather than assumed. The findings were
concrete.

- `Microsoft.EntityFrameworkCore.Sqlite.NetTopologySuite` 10.0.12 pulls in a `mod_spatialite` native
  package whose runtimes folder contains **`win-x64` and `win-x86` only**. The `linux-x64` runtime in
  that folder contains `libe_sqlite3.so` and no `mod_spatialite.so` at all.
- Both `ci.yml` and `pages.yml` run on `ubuntu-latest`. The platform that builds, tests, and
  publishes this project is therefore the platform where the native library does not exist, unless
  the workflow installs a system package.
- Loading it on Windows works but is not simply a matter of calling `LoadExtension`. The extension
  depends on sibling libraries (geos, proj, iconv) that the OS loader resolves against the process
  search path, so the runtime native directory must be prepended to `PATH` first. With that done,
  `spatialite_version()` returns `4.3.0a`.
- Geodesic distance additionally requires the spatial reference tables. `Distance(…, 1)` with SRID
  4326 fails with `unknown SRID: 4326 <no such table: spatial_ref_sys>` until `InitSpatialMetaData`
  has populated several megabytes of reference data into the database file.

A spatial query path that cannot run where the project actually runs is not a spatial query
capability, and shipping one exercised on a single developer's operating system would be claiming
coverage this repository does not have.

## Decision

Query in two stages, both of which do real work.

**Stage one narrows, and is indexable.** A search circle is converted to the smallest latitude and
longitude rectangle that contains it, and that rectangle becomes a SQL predicate served by a
composite index on the incident's coordinates. A database cannot index a great-circle distance;
it can index latitude and longitude, and this is what makes a chokepoint search touch a band of rows
rather than the whole table.

**Stage two decides, and is exact.** A rectangle is not a circle — its corners reach roughly 1.4
times the radius — so every row the box admits is measured with a great-circle distance and rejected
if it falls outside. The box may over-admit; it can never exclude a row the circle would have
accepted, which is the property the whole arrangement rests on and which is asserted at 72 bearings
around a test circle.

`ISpatialQueryService` is the seam. It exposes a `Method` string that travels into the API payload
and the published snapshot, so a consumer is told how a distance was obtained rather than left to
assume a precision the backend does not have. A deployment with a working spatial extension can
implement the same interface against SQL functions without any caller changing.

Maritime chokepoints are a short curated catalogue behind `IChokepointCatalogue`, each with its own
watch radius: a canal is a few kilometres wide and a strait approach is a hundred, so a single radius
would either miss the approaches to one or sweep unrelated activity into the other.

## Consequences

- Spatial queries work identically on Windows, on Linux, in CI, and in the published snapshot, with
  no native dependency and no platform-specific setup.
- The bounding box is a domain concept with its own invariants, and the awkward cases are handled
  rather than ignored. A box that spans the 180th meridian is two longitude intervals, not one, and
  expressed as a single `BETWEEN` it would select everything except the region wanted. A circle
  reaching a pole spans every longitude, which is correct rather than degenerate.
- Chokepoint output is counts, severity breakdowns, and distances — not a score. The payload states
  in its own body that proximity is geography and not an assessment of threat, so the claim survives
  being read through the API rather than through the UI that happens to render it.
- Quiet chokepoints are still reported with a count of zero. "Nothing recorded here" is an answer,
  and a watch list that changed length between requests would be unreadable.
- This is not SpatiaLite, and the README says so with the reason rather than implying the capability.
  The measured facts above are recorded here so the decision can be revisited rather than re-derived.
- The trade-off is real: the portable path pulls candidate rows into memory to measure them, where a
  spatial extension would do it in SQL. At this data volume that is irrelevant, and the candidate
  count is bounded. At a volume where it mattered, the interface is where the replacement goes.
- A satellite thermal detection near a reported incident now shows up in the same chokepoint panel
  even though the correlator will not merge the two, because correlation pre-filters by event type
  (ADR 016). Spatial proximity and incident correlation answer different questions, and the
  chokepoint view is where the corroborative value of satellite data actually becomes visible.

## Revisited 2026-09-14: the trigger that ends this decision

The consequence above says "at a volume where it mattered, the interface is where the replacement
goes". Sprint 17 was asked to name the volume, as a measurement rather than a date, and the answer
turned out to be sharper than a latency threshold.

**The trigger is a spatial search returning the full candidate cap.**

`SpatialQueryService` pulls at most 1,000 rows from the rectangle before measuring exact distances
over them, and that cap is what bounds the cost of a deliberately wide search. Below it the two
stages together give a *complete and exact* answer: the rectangle narrows, the great-circle distance
decides, and the price is a few hundred rows of trigonometry.

At the cap the arrangement stops answering the question it was asked. The rows beyond it are never
measured, so "incidents within 50 km of here" silently becomes "the most recent thousand inside the
rectangle, then filtered by distance", and the chokepoint panel's count becomes the cap rather than a
count. The failure is one of **correctness, and it is silent** — which is the worst combination
available and the reason this is the trigger rather than a latency figure.

It is also not fixable inside this decision. Raising the cap trades a wrong answer for a slow one.
Tuning the index does nothing, because an index can narrow a rectangle and cannot narrow a distance;
narrowing a distance is exactly the capability a spatial index provides and this does not have.

So every search now records how many rows its rectangle returned, and the operations report publishes
the count, the largest rectangle so far, and whether any search has reached the cap. Sprint 14 owns
the migration to PostGIS; this records the number that starts it, and the page states it the moment
it is reached rather than leaving it to be noticed.
