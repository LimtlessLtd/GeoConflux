# Dependency review

Date: 2026-09-12. Scope: every NuGet package the solution references, directly or transitively, plus
the two runtime assets the dashboard fetches from a CDN and the four GitHub Actions the workflows
invoke.

Every figure below came from a command that was actually run; the commands are named so the review
can be repeated rather than believed.

## What is here

25 direct package references across eight projects, resolving to 465 packages once transitive
dependencies are included.

```powershell
dotnet list GeopoliticsDashboard.sln package --vulnerable --include-transitive
dotnet list GeopoliticsDashboard.sln package --deprecated
dotnet list GeopoliticsDashboard.sln package --outdated
```

Every direct reference comes from one of four publishers — Microsoft, the .NET Foundation
(`xunit`), the OpenTelemetry project, and `coverlet`. There is no package here from a single-author
or low-traffic source, which is the supply-chain risk this kind of review is mostly looking for. That
is a consequence of earlier design decisions rather than luck: the provider abstraction, the queue,
the gazetteer, the correlator, and the spatial maths are all written against framework primitives
instead of pulled in.

## Vulnerabilities

**None.** `--vulnerable --include-transitive` reports no advisory against any of the 465 packages, in
any project.

## Deprecations

One, and it is test-only.

| Package | Version | Status | Decision |
| --- | --- | --- | --- |
| `xunit` | 2.9.3 | Legacy; `xunit.v3` is the stated successor | Deferred, deliberately |

xunit v3 is not a drop-in upgrade. It changes the assembly model, the runner contract, and API
surface that 305 tests in this repository use — `TestContext.Current.CancellationToken` replacing
ambient patterns is one example already met while writing the new security tests. Migrating is a
whole increment of work whose entire visible outcome is that the tests still pass.

Against that: xunit 2.9.3 has no advisory, is not a shipped dependency, and runs only on a developer
machine and on CI. "Legacy" here marks a successor existing, not a defect.

So it stays, and it stays *recorded* rather than quietly ignored. If it ever acquires an advisory the
calculation changes immediately, which is the point of writing the reasoning down instead of just the
conclusion.

## Versions

Every package that ships in the application is current. Nothing in `Geopolitics.Domain`,
`Geopolitics.Application`, `Geopolitics.Infrastructure`, `Geopolitics.Api`, or `Geopolitics.Workers`
has an update available.

The only updates offered are in the three test projects:

| Package | Current | Latest | Decision |
| --- | --- | --- | --- |
| `Microsoft.Extensions.TimeProvider.Testing` | 10.0.0 → **10.10.0** | 10.10.0 | **Updated** |
| `Microsoft.NET.Test.Sdk` | 17.14.1 | 18.10.0 | Deferred |
| `xunit.runner.visualstudio` | 3.1.4 | 4.0.0 | Deferred |
| `coverlet.collector` | 6.0.4 | 10.0.1 | Deferred |

`TimeProvider.Testing` was updated because it was the one genuine inconsistency: the solution already
uses the 10.10.0 band of `Microsoft.Extensions.*` for AI and HTTP resilience, and holding one package
of the same family at 10.0.0 invites exactly the assembly-version mismatch that is tedious to
diagnose later. The full suite passes on 10.10.0.

The other three are deferred together and for one reason: they are the test *runner*, they are all
major-version jumps, and the sensible time to take them is alongside the xunit v3 migration above,
since `xunit.runner.visualstudio` 4.0 exists to serve it. Taking them piecemeal now would mean
absorbing the risk of a runner upgrade twice.

`coverlet.collector` deserves a specific note, because it is the one package here that nothing
currently invokes: CI runs `dotnet test` without `--collect`, so the collector never activates. It is
kept rather than removed because it is a test-time collector that never reaches a shipped artefact,
and because coverage is then one command away rather than one dependency change away. It is listed
here so that the fact it is dormant is on the record, rather than being discovered by someone
wondering why it is in the graph.

## Runtime assets fetched from a CDN

Two, both from `cesium.com`, both pinned to an exact release (1.126) rather than to `latest`:

- `Build/Cesium/Cesium.js`
- `Build/Cesium/Widgets/widgets.css`

Globe imagery comes from `services.arcgisonline.com` at runtime.

These are outside NuGet's reach and so outside every command above, which is precisely why they are
listed. Three things constrain them. The version is pinned, so the bytes do not change when Cesium
ships a release. The content security policy restricts script, style, image, and connection sources to
these two hosts and nothing else. And the dashboard degrades rather than breaks if either is
unreachable: the globe falls back to a bundled offline texture and says so, and the incident list and
feed keep working.

What is *not* in place is subresource integrity, which would pin the bytes rather than the source.
The reasoning is in the [security review](security-review.md#not-done-and-why).

## GitHub Actions

Four, all from the `actions` organisation, all now pinned to commit SHAs rather than to the moving
major tags they previously used:

| Action | Pinned SHA | Release |
| --- | --- | --- |
| `actions/checkout` | `fbc6f3992d24b796d5a048ff273f7fcc4a7b6c09` | v5.1.0 |
| `actions/setup-dotnet` | `26b0ec14cb23fa6904739307f278c14f94c95bf1` | v5.4.0 |
| `actions/upload-pages-artifact` | `56afc609e74202658d3ffba0e8f6dda462b719fa` | v3.0.1 |
| `actions/deploy-pages` | `d6db90164ac5ed86f2b6aed7e0febac5b3c0c03e` | v4.0.5 |

`actions/checkout` was moved from v4 to v5 in the course of this review: pinning it made the runner
report that v4 targets the deprecated Node 20 and was being forced onto Node 24. A pinned dependency
that a runner has to paper over is not really pinned, so it was taken to the release that runs on the
runtime the runner actually provides.

A tag is a pointer its owner can move; a SHA is not. Since an action runs with the workflow's token,
and the Pages workflow's token can publish the site, the distinction is worth the small cost of
updating a hash when these are deliberately upgraded.

## How to repeat this

```powershell
dotnet list GeopoliticsDashboard.sln package --vulnerable --include-transitive
dotnet list GeopoliticsDashboard.sln package --deprecated
dotnet list GeopoliticsDashboard.sln package --outdated
```

The first of these is the one that should be run routinely. The other two produce advice; that one
produces facts.
