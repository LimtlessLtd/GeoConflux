# ADR 024: The dashboard is tested with Node's own test runner, and with nothing else

- Status: Accepted
- Date: 2026-09-12
- Closes the gap recorded in [the architecture review](../architecture-review.md), which stated that
  the dashboard had no automated test coverage and that fixing it was an increment of its own.

## Context

`wwwroot/app.js` was 78 KB of application logic with nothing in `tests/` referencing it. Three things
in that file are load-bearing:

- the `escapeHtml` discipline in the render paths, which is the first half of the defence against a
  hostile feed title reaching the DOM as markup — the second half being the content security policy;
- the fallback from a live backend to the exported snapshot, which is the only reason the published
  page works at all, since GitHub Pages serves files and cannot run the API;
- the relative-time basis, which decides whether a record is aged against the reader's clock or
  against the recorded run, and therefore whether a synthetic event on a real strait reads as though
  it happened this afternoon.

All three had been verified by hand in a real browser, including against the published page. That is
genuinely how the content policy and the globe fallback were checked, and it is worth something. But
hand verification does not survive a refactor, and this is the one part of the system where a
regression reaches a reader directly rather than being caught by a pipeline test.

The blocker was never whether to test it. It was that testing browser code from a .NET solution
forces a dependency decision, and that decision deserved recording rather than being made silently by
whoever typed `npm install` first.

## Decision

### Logic that does not need a browser moves out of the file that does

`wwwroot/lib/` holds pure functions: escaping, the severity vocabulary, time bases, provenance
wording, incident filtering, source selection, and the classification chips. `app.js` keeps
everything that reaches for the DOM, the network, or the globe, and imports the rest.

The line is drawn at *needs a document*, and nowhere else. A helper moves out when it stops needing
one, not because the file looked tidier afterwards. This matters because the alternative split —
"move out what is easy to test" — drifts until the boundary means nothing.

`app.js` keeps its immediately-invoked wrapper even though a module already has its own scope. It is
redundant, and it is kept so that the file's structure and indentation still match the version that
was verified by hand, which makes the change reviewable as a move rather than a rewrite.

### The runner is `node:test`, and there are no dependencies

The tests use `node:test` and `node:assert`, both of which ship with Node. The repository gains a
`package.json` that declares ESM and a test script, and **no dependencies, no lockfile, and no
third-party code**.

That is the whole point of the choice. This repository treats external payloads as untrusted input
and pins its CI actions to commit SHAs rather than to moving tags; adding a few hundred transitive
packages to assert that a function escapes a quote character would be inconsistent with both. A
dependency tree is a supply chain, and this one would exist solely to run the tests — a category of
dependency that has been a real attack path against exactly this kind of project.

### The tests run as their own CI step, not through `dotnet test`

`dotnet test` continues to mean "the .NET suites". The dashboard suite is a separate step in both
workflows, and both are required before the page is published.

Fusing the two — an MSBuild target shelling out to `node` — was rejected because it would make
`dotnet build` fail on a machine without Node, for a project whose documented local workflow is
otherwise entirely .NET. The cost of that is one more command to run locally, which is stated in
`CLAUDE.md` and in the README rather than left to be discovered.

## Alternatives considered

**Vitest or Jest with jsdom.** The obvious choice, and the one that would let the DOM-facing code be
tested as well as the pure logic. Rejected on the dependency grounds above, and on a second ground
that matters more: a jsdom suite would have let the untestable structure stand. The reason none of
this was tested was that it was all one closure with no exports, and a DOM emulator would have made
that tolerable instead of fixing it. Pulling the logic out is the change with the longer half-life,
and it is worth noting that it is *also* what would make adopting jsdom cheap later, if the
DOM-facing code ever justifies it.

**Playwright or another browser driver.** The right tool for what is still uncovered — the globe, the
SignalR reconnection, the replay control — and far too heavy for this increment. It needs a browser
download, a server to point at, and a CI budget measured in minutes rather than the 0.4 seconds this
suite costs. It is the natural next increment if the dashboard grows, not this one.

**Leaving it hand-verified.** Rejected, but it is worth being precise about why, because the hand
verification was real. It does not survive a refactor by someone who was not there for it, and the
extraction in this very change is an example: seven deliberate mutations were introduced afterwards
to confirm the suite catches them, and it caught all seven. Nothing about a browser check would have.

## Consequences

**The page is now a module graph rather than one script.** `index.html` loads `app.js` with
`type="module"`. Module scripts defer, which if anything makes the ordering against the Cesium global
safer, and the content policy already allowed same-origin scripts.

**A missing file now breaks the whole page, where before there was nothing to miss.** This is the
real cost of the split, and it is mitigated in two places: a test reads the actual `app.js`, resolves
every import it makes, and asserts the named exports exist; and the publish workflow verifies each
module file is present and non-empty in the built output before it deploys.

**Some of the file is still untested, and this ADR does not claim otherwise.** Rendering, the globe,
the SignalR client, the replay timer and the analytics fetches remain verified by loading the page.
What the 81 tests cover is the logic that decides what those render paths are told to say.
