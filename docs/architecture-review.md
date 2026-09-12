# Final architecture review

Date: 2026-09-12. Scope: the whole repository, reviewed against the twenty dimensions the
specification names. Security is dimension twelve and was reviewed separately in
[the security review](security-review.md); dependencies likewise in
[the dependency review](dependency-review.md). This document covers the other eighteen and does not
repeat those two.

The brief was to review hostilely and not to praise the repository. The useful form of that is not a
long list of complaints — it is a short list of things that are actually wrong, each demonstrated,
plus an honest account of what was examined and found sound, because a review that reports only
problems gives no way to tell the difference between a clean area and an unexamined one.

Seven defects are recorded below. Every one was reproduced by a test that fails against the code as
it stood and passes after the change; the failing output is quoted where it is the clearest statement
of the problem. Nothing here was inferred from reading alone. Several things that looked like
defects turned out not to be, and those are recorded too, in [Examined and left
alone](#examined-and-left-alone), along with what settled the question.

**No Critical findings.** Nothing found can corrupt committed data, expose a credential, or take the
host down. Saying so plainly is more useful than promoting a High to fill the row.

## Findings

| # | Severity | Dimension | Summary |
| --- | --- | --- | --- |
| 1 | High | concurrency, async processing | A concurrent redelivery was reported as a pipeline failure and its evidence discarded |
| 2 | High | database design, resilience | The failure-recovery path re-committed the unit of work it was recovering from |
| 3 | High | AI architecture, resilience | The severity model could fail an observation, contradicting its own stated guarantee |
| 4 | Medium | concurrency, performance | The correlation lock was held across the realtime fan-out |
| 5 | Medium | observability | The per-stage cost breakdown charged persistence and publication to correlation |
| 6 | Medium | external integration, validation | Poll intervals and batch sizes reached the network unvalidated |
| 7 | Low | documentation | An orphaned doc comment left one method undocumented and another with two summaries |

---

### 1. High — a concurrent redelivery was reported as a failure, and its evidence thrown away

**Where.** `EfIncidentRepository.SaveChangesAsync`, and the `DuplicateObservationException` handler in
`ObservationProcessor.ProcessAsync`.

**What was wrong.** Deduplication has two layers, and the design is explicit that the second is the
real one: a read-then-insert check catches the ordinary case, and a filtered unique index on
`Fingerprint` settles the race that the check cannot, because two workers holding the same payload
both pass it without seeing each other's uncommitted write. `EfObservationRepository.SaveChangesAsync`
translated that constraint violation into `DuplicateObservationException`, and the processor had a
handler for it.

The handler was unreachable. In the committing path the observation, the incident, and their linkage
are saved together in one unit of work, and that save is issued through `incidentRepository` — which
did not translate anything. So the loser of the race got a raw `DbUpdateException` instead, fell into
the generic failure handler, and was counted, logged, and reported as a broken observation.

The recovery then failed as well. It retried the same insert against the same index, hit the same
violation, and logged that the source payload was lost — which it was.

This is live in the shipped configuration. Both hosts set `ProcessorConcurrency: 2`.

**Why it matters.** Three ways. The reported outcome is wrong: a duplicate is an expected condition
the pipeline is built to handle, and a failure is an error a person is meant to read. The metrics are
wrong in both directions — `pipeline.items.failed` counts something that did not fail, and
`events.deduplicated` misses something that was. And evidence is destroyed, which the domain is
emphatic it must never be: `RawObservation`'s own summary says an observation is retained "even when
it is a duplicate … so that evidence is never destroyed by a downstream failure".

**Demonstrated by.** `PipelineFailureBoundaryTests.AConcurrentRedeliveryIsReportedAsADuplicateRatherThanAFailure`,
which runs two real processor scopes against real SQLite, held at a barrier so both candidate reads
are genuinely in flight. Before the fix:

```text
OUTCOMES = Persisted, Failed:"An error occurred while saving the entity changes."
```

The existing `ConcurrentCorrelationTests` did not catch this, and could not: it varies the source
identifier per report specifically so that every payload has a *distinct* fingerprint, because what it
tests is correlation rather than deduplication. The gap was exactly the case it deliberately excluded.

**Fix.** The translation moved from `EfObservationRepository` onto `GeopoliticsDbContext.SaveChangesAsync`.
It is a property of the database, not of whichever repository a caller happened to reach for, and
putting it on the context makes it true for every save through it — including any repository added
later, which is how this would otherwise recur.

The processor's handler now also retains the losing delivery, marked as the duplicate it turned out
to be, so the concurrent and sequential paths agree. Whether a repeat is spotted by the read or by the
index is a timing accident and should not decide whether the delivery stays auditable. The filtered
index exempts rows already marked as duplicates, so the schema already permitted this.

`RawObservation.MarkDuplicate` now also clears `IncidentId`. Usually that is already null, because a
duplicate is normally recognised before correlation runs. It is not null here: by the time the race is
lost, the observation has been linked to an incident that was rolled back with the failed save, and
keeping the link would leave the row pointing at a record that does not exist.

---

### 2. High — the recovery path re-committed the work it was recovering from

**Where.** `ObservationProcessor.TryRetainFailedObservationAsync`.

**What was wrong.** After any processing failure, the processor keeps the source payload so the report
can be diagnosed and reprocessed. That intent is right. The implementation marked the observation as
failed, added it, and called `SaveChangesAsync` — on the same context, which still held every pending
change from the attempt that had just failed.

EF Core does not roll back the change tracker when a save fails. Entities stay Added and Modified. So
the retention save was not saving one row; it was re-attempting the entire failed unit of work, with
the observation's status flipped to `Failed` on the way. When the original failure was transient, the
retry succeeded — and committed an incident assembled from an observation the pipeline had just
reported as failed and would never announce.

**Why it matters.** The result is a record in the database that nothing in the system believes in.
The observation says `Failed`; the incident counts it as evidence; the API serves the incident and
the analytics count it; no realtime message was ever sent for it, because publication happens only on
the success path. It is a silent, self-inflicted inconsistency produced by the error handler.

**Demonstrated by.** `PipelineFailureBoundaryTests.AFailedCommitDoesNotLeaveTheIncidentBehind`, which
injects one failing save into the real repository through a decorator, so the change tracker, the
transaction, and the recovery path under test are all production code. Before the fix, the observation
was retained as intended *and* the incident was committed alongside it:

```text
Assert.Empty() Failure: Collection was not empty
```

**Fix.** A new repository operation, `IObservationRepository.RetainEvidenceAsync`, which detaches the
tracked incidents and then commits. The call site cannot express this — that is the reason it is a
repository concern rather than an `Add` followed by a `SaveChanges`.

The first version of this fix cleared the change tracker outright, and that was wrong in a way worth
recording, because it was only caught by asking what else was in flight. Enrichment audit rows are
staged in the same unit of work, and they carry no foreign key *deliberately*, so that the record of
having called a model survives the observation it describes. Clearing the tracker would have quietly
reversed that decision on the one path where it matters most. Detaching incidents specifically keeps
the evidence and drops only the derived state. The test asserts both halves, so a later simplification
back to a blanket clear will fail rather than pass quietly.

---

### 3. High — the second opinion could fail the observation it was only supposed to comment on

**Where.** `ObservationProcessor.RecordModelSeverityAsync`, and `MLNetSeverityModel.TryTrain`.

**What was wrong.** The trained severity model is a recorded second opinion with no authority over
anything, and the method that calls it says so at length: "an unavailable model, a slow one, or one
that throws all produce the same outcome as a disabled one: no prediction, and the pipeline
continues." Every failure inside the `try` was indeed swallowed.

The readiness check sat outside it.

`ISeverityModel.IsReady` is not a field read. In the shipped implementation it is what forces the lazy
training to run, so it is the member most likely to throw — and because the training sits behind a
`Lazy<T>` with `ExecutionAndPublication`, an escaping exception is cached and rethrown to every
subsequent caller. One failure would therefore fail *every* observation from then on, not one.

`TryTrain`'s catch filter made that reachable. It listed `InvalidOperationException`,
`ArgumentException`, and `FormatException` — the exceptions a bad dataset produces — and so did not
cover the ones a bad platform produces. A missing native dependency surfaces as `DllNotFoundException`
or `TypeInitializationException`, and neither was caught. On a host where ML.NET's native libraries are
absent, the outcome was not "no second opinion" but "every observation fails".

The exception handler had the same flaw: it tags its metric with `severityModel.Version`, which reaches
the same lazily trained value, so the handler meant to contain a training failure would itself throw
one.

**Why it matters.** This is the inversion the design exists to prevent. A supplementary classifier
fitted to a small synthetic corpus had become able to stop the ingestion of real reporting, and the
code asserting otherwise sat directly above it — which is the more dangerous kind of wrong, because it
tells a reviewer to stop looking.

**Demonstrated by.** `SeverityModelPipelineTests.AModelThatCannotSayWhetherItIsReadyDoesNotFailTheObservation`.
The existing `AThrowingModelDoesNotFailTheObservation` covers a model that throws from `PredictAsync`
and passes; the new one throws from `IsReady`, and before the fix returned `ProcessingOutcome.Failed`.

**Fix.** Both ends. `TryTrain` now catches unfiltered, which is the unusual choice and the correct one
here: the class already states that a model which cannot be fitted must disable itself rather than fail
the host, and this applies that decision to every way it can happen rather than to the three that were
anticipated. The processor guards the readiness check and the version read as well, because
`ISeverityModel` is an interface and the pipeline should not depend on one implementation being
well-behaved.

---

### 4. Medium — the correlation lock was held across the realtime fan-out

**Where.** `ObservationProcessor.RunStagesAsync`.

**What was wrong.** The correlation gate serialises correlate-then-commit for one event category, and
both the gate's own documentation and the comment at the call site describe it as held "from the
candidate read through to the commit". It was declared with `using var`, so it was actually released
at the end of the method — after persistence, and after the SignalR publication.

**Why it matters.** Publication is a fan-out to every connected client, and its duration is set by the
slowest of them rather than by anything the pipeline controls. Holding a per-category lock across it
lets one stalled browser serialise the processing of every later observation in that category. The
lock exists to protect a read-then-write against the database; once the transaction has committed
there is nothing left for it to protect, which is precisely why the comment said what it said.

**Fix.** The gate is now a scoped block ending at the commit, and publication moved outside it. This is
the code being brought into line with its own documented design rather than a new decision.

---

### 5. Medium — the per-stage cost breakdown charged persistence and publication to correlation

**Where.** `ObservationProcessor.RunStagesAsync`, the same declaration as finding 4.

**What was wrong.** `correlateStage` was also declared with `using var`, and `StageScope` records its
duration on dispose. The persist and publish stages open and close inside that lifetime, so the
`pipeline.stage.duration` histogram attributed their cost to `pipeline.correlate`, and the
`pipeline.correlate` span enclosed them in a trace instead of sitting beside them.

**Why it matters.** The stage breakdown exists to answer "which part is slow" — that is its stated
purpose, and it is what the tracing work in `33ffb0a` was for. A decomposition whose parts sum to more
than the whole they decompose does not answer that question; it points at correlation whatever the real
cost is. This is the failure mode instrumentation is most prone to, because nothing breaks when it is
wrong.

**Demonstrated by.** `ObservabilityTests.EachStageIsMeasuredOnItsOwnRatherThanInsideTheStageBeforeIt`,
asserting that each stage span's parent is the processing span. The existing test in that file asserts
only that the stage spans share a trace identifier, which is true either way — that is the gap that let
this through, and the new test is deliberately separate from it rather than folded into it.

**Fix.** Carried by the same restructuring as finding 4: the correlate scope now closes before persist
begins, making the seven stages siblings under `pipeline.process`.

---

### 6. Medium — poll intervals and batch sizes reached the network unvalidated

**Where.** `ProviderOptionsValidator`, and `PollingEventSource.ReadAsync`.

**What was wrong.** Feed URLs are validated at startup, for the stated reason that a deployment
decision which is wrong should stop the deployment. The two settings that govern *how often* this
process contacts a third party were not validated at all.

Both boundary values were checked against the runtime rather than reasoned about:

```text
zero:     completed immediately (hot loop)
negative: threw ArgumentOutOfRangeException
```

A `PollInterval` of zero makes `Task.Delay` return immediately, turning the poll loop into a hot loop
that requests as fast as the network allows — a flood of somebody else's API, issued in this project's
name. A negative value throws from inside the iterator, where only `OperationCanceledException` is
handled; it escapes into the pump's containment handler, and the source is dead for the lifetime of the
process, having produced one log line.

`MaxItemsPerPoll` of zero is not a smaller batch. It is a source that fetches from the network every
cycle and discards the answer, which is indistinguishable from a dead feed on the dashboard.

**Why it matters.** The first is the more serious: a misconfigured deployment of this project becomes
abusive toward a public service it does not own. The second and third share the failure mode the
existing URL validation calls out as the hardest to notice — a source that silently produces nothing.

**Demonstrated by.** `ProviderSecurityTests.APollIntervalThatCannotBeWaitedOnStopsStartup` (zero and
negative) and `.ABatchSizeOfZeroStopsStartup`.

**Fix.** Both are validated at startup for all three adapters, beside the URL checks that were already
there, with messages that say what the value would actually do rather than only that it is invalid.

---

### 7. Low — an orphaned doc comment

**Where.** `RawObservation`.

**What was wrong.** Two `<summary>` blocks were stacked on `RecordModelSeverity`. The first belonged to
`MarkValidated`, and had been separated from it when the model-severity members were inserted between
them. `MarkValidated` was left undocumented and `RecordModelSeverity` carried a summary describing a
different method.

**Why it matters.** Barely, on its own — no behaviour depends on it. It is recorded because this is a
repository where the comments are load-bearing: several findings above were found by noticing that a
comment and its code disagreed, and that only works while the comments are trustworthy.

**Fix.** The comment is back on `MarkValidated`.

---

## Examined and left alone

Recorded because "not mentioned" and "checked and fine" are different claims, and a reader cannot tell
them apart otherwise.

**Domain modelling — not anemic.** `RawObservation` and `GeopoliticalIncident` have private setters, a
private parameterless constructor for the persistence layer alone, and enforce their invariants in
methods that carry the reasoning: `RecordAssessment` adopts a classification only when it is better
supported than the one held, `MergeEntities` caps an unbounded list built from untrusted text, and
`ApplyExtractions` exists so that low confidence in a classification does not discard the factual
extractions alongside it. The state machine is expressed in the aggregate rather than in the service
that drives it.

**Dependency direction.** `Geopolitics.Domain.csproj` has no package references and no project
references. Domain → Application → Infrastructure → API/Workers holds, and the build enforces it.

**Abstractions.** Nineteen interfaces in `Application/Abstractions`, and each has either more than one
real implementation or a real substitution in tests: `IEventSource` has four, `ISeverityModel` has the
trained model and the disabled case, `IIncidentNotifier` has SignalR and null implementations — the
latter being what lets the Workers host demonstrate that processing does not depend on a connected
browser. No interface-per-class-for-its-own-sake, and no repository wrapping a repository.

**SignalR.** `IncidentHub` has no server-callable methods at all, which makes it an outbound projection
rather than an entry point into the pipeline. Publication happens after the transaction commits, and a
notifier fault is caught, counted, and logged without affecting stored state. This is the correct shape
and the reason finding 4 was worth fixing rather than worth living with.

**Indexes.** Every index present is matched to a query that exists: `(EventType, OccurredAt)` serves the
correlator's candidate pre-filter, `(Latitude, Longitude)` serves the spatial bounding box with the
selective column leading, `(Outcome, CreatedAt)` serves the provider-health question, and the filtered
unique index on `Fingerprint` is the authority behind deduplication. No index without a query, and no
query in the hot path without an index.

**The chokepoint loop is not an N+1.** `AnalyseChokepointsAsync` issues one query per chokepoint. N is
eight, fixed by a compile-time catalogue, and does not grow with data — which is the property that makes
an N+1 an N+1. Each query is an indexed range scan. Left alone.

**The exporter's evidence loop is an N+1, and was still left alone.** `SnapshotExporter.WriteAsync`
queries evidence once per incident, and N *does* grow with the data there. It is bounded at 250 by the
caller's own `IncidentSearch(250)`, every query is served by the `IncidentId` index, and the whole thing
is a build step rather than a request path — a measured export runs in 8.3 s end to end, dominated by
host startup and the pipeline run rather than by these lookups. Replacing it with a batched query would
add a repository method and a migration of the call site to save milliseconds in CI. Recorded rather
than done, because the specification is explicit that cosmetic changes made to produce more code are not
wanted.

**Analytics issues nine sequential queries per report, and that is forced.** They share one
`DbContext`, which is not thread-safe, so `Task.WhenAll` over them would be a bug rather than an
optimisation. Every count is a `GROUP BY` the database evaluates, and the one query that returns rows
projects four scalar columns under a cap whose truncation is reported in the response rather than
hidden. The sequencing is a consequence of the unit-of-work design, not an oversight in it.

**Cancellation and shutdown.** Every `async` method on the pipeline path takes and honours a token.
`OperationCanceledException` is distinguished from failure at every handler that matters, so shutdown
does not write misleading failure rows. `EventSourcePumpService.StopAsync` completes the queue so the
processor drains and exits rather than waiting on a writer that will never produce again, and the queue
is deliberately *not* completed when finite sources end, so manual submission keeps working.

**Correlation.** Reviewed closely, since a wrong merge destroys information and is nearly invisible once
committed. The design is defensible and documented: positional corroboration is mandatory, the
confidence is a ceiling set by positional evidence rather than a weighted mean that lets a strong text
match compensate for an absent location, country-centroid matches are explicitly refused as evidence of
co-location, and the time signal was deliberately removed after it degenerated to a constant against
live feeds. The reasoning is recorded at the point of decision.

**Coordinates are never set by a model.** Confirmed end to end. `ApplyEnrichment` and `ApplyExtractions`
accept a location *name* and no coordinate. `GazetteerLocationResolver` produces positions from
provider-declared coordinates or from a lexicon lookup, never from enrichment output. The precision of
the result is carried with it, so a country centroid is not drawn as though it were a street address.

**Performance.** `PipelineThroughputTests` asserts per-item cost is flat rather than asserting a wall
clock, which is the right shape for a test that has to pass on shared CI hardware.

## The one real gap left open — since closed

The dashboard has **no automated test coverage at all**. `wwwroot/app.js` is 78 KB of application logic
— the render paths whose `escapeHtml` discipline is the first half of the XSS defence, the snapshot
fallback, the relative-time basis that decides whether a record ages against the reader's clock or the
recorded run — and nothing in `tests/` references it. It has been verified by hand in a real browser,
including against the published page, and that is genuinely how the content security policy and the
globe fallback were checked. But hand verification does not survive a refactor, and this is the one part
of the system where a regression reaches a reader directly.

This was not fixed in the review itself. Adding a JavaScript test runner to a .NET solution is an
increment with its own dependency decision to make and record, not a change to fold into a review, so
it was stated as an open gap rather than left for someone to discover.

**It has since been closed.** [ADR 024](adr/024-dashboard-test-runner.md) records the decision. The
logic that does not need a browser moved to `wwwroot/lib`, and 81 tests now cover it using
`node:test` — no dependencies, no lockfile, no third-party code. All three things named above are
among them: the escaping discipline is pushed hostile input and checked for markup, the snapshot
fallback is exercised against sources that return false and sources that throw, and the relative-time
basis is asserted to give a demo record the same answer whether the page is read the same day or the
following year.

Seven deliberate mutations were introduced afterwards to confirm the suite catches them — a dropped
quote character in `escapeHtml`, a basis that always uses the reader's clock, a demo notice that
ignores a snapshot's own declaration, an empty database read as live, a reversed sort, an unescaped
chip, and a renamed export — and all seven failed the suite. That is the property the gap was about:
hand verification would have caught none of them.

What remains uncovered is stated in the ADR rather than implied here: rendering, the globe, the
SignalR client, the replay timer and the analytics fetches are still verified by loading the page. The
suite covers the logic that decides what those render paths are told to say, which is where a
regression reaches a reader as a false statement rather than as a visibly broken screen.

## Verification

Run after every change above:

```powershell
dotnet build GeopoliticsDashboard.sln          # 0 warnings, 0 errors
dotnet test GeopoliticsDashboard.sln           # 312 passed, 0 failed
npm test                                       # 81 passed, 0 failed (dashboard client)
dotnet format GeopoliticsDashboard.sln --verify-no-changes
```

312 .NET tests: 260 unit, 10 AI evaluation, 42 integration. Seven of them are new here, and all seven
failed against the code as it stood. The 80 dashboard tests are a separate suite and a separate
command, for the reason [ADR 024](adr/024-dashboard-test-runner.md) gives: `dotnet test` keeps
meaning "the .NET suites", and `dotnet build` does not start depending on Node being installed. `docker build` runs on CI rather than locally, because Docker is not
installed on the machine this review was performed on — stated rather than reported as passing.

The two concurrency tests were run five times consecutively to check they are not timing-flaky, since
both depend on a barrier releasing several workers into a real database at once. Five clean runs.
