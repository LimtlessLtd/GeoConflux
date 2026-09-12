# ADR 022: Running without authentication is paid for with limits, not assumed to be free

- Status: Accepted
- Date: 2026-09-12
- Related to [ADR 003](003-processing-queue.md), which chose the bounded queue whose backpressure
  behaviour this decision qualifies.

## Context

The specification says no authentication system is required unless a feature genuinely needs one, and
warns against building an identity system for the portfolio. That is the right call: nothing here
needs accounts, and an unnecessary login page would be a worse answer to "show me your judgement"
than not having one.

But "no authentication" is a decision with a bill attached, and the Sprint 6 review found it unpaid
in two places.

**The open write path could hold the server.** `POST /api/observations` is the only endpoint anyone
on the network can write to. It enqueues onto the bounded channel from ADR 003, which uses
`BoundedChannelFullMode.Wait`: when the queue is full, producers block. That is exactly right for a
polling adapter — a feed that cannot be enqueued should slow down, not be dropped. It is exactly
wrong for an HTTP request, because a request that waits is a connection held open, and nothing
limited how many an unauthenticated caller could open at once. A saturated pipeline was expressible
as unbounded held connections. A large body was also read and parsed in full before the
20,000-character content check refused it.

**The dashboard's escaping had nothing behind it.** Every render path in `app.js` passes untrusted
text through `escapeHtml`, and no feed-supplied value reaches an `href`, `src`, or script sink. That
was checked and it holds. The problem is that it was the *only* thing between a stranger's headline
and script execution, and it is a property that must hold at every render call, forever, including
the next one somebody adds.

## Decision

### Backpressure stops at the HTTP boundary

The submission endpoint bounds how long it will wait for queue space. Five seconds, then `503` with
an explanation of what happened.

The bound is at the endpoint rather than in the queue, and that distinction is the decision. The
adapters *should* keep blocking indefinitely: backpressure is how a polling source learns to slow
down, and removing it would trade a bounded wait for dropped observations. What differs about the
HTTP caller is not the queue, it is that a waiting caller costs a connection. So the queue keeps its
semantics and the endpoint declines to use all of them.

`503` rather than `500`: the request was valid and the caller is still there. It is this process that
could not take the work, and saying so is both true and actionable.

### The open endpoint is rate limited, and only that endpoint

A fixed-window limiter partitioned by remote address, rejecting immediately rather than queueing —
holding a request to release it later is the same resource problem this exists to prevent — and
returning `Retry-After` so a caller learns when to come back instead of merely that it was refused.

It applies to the submission endpoint alone. The read endpoints are cheap, bounded by their own
`take` clamps, and rate-limiting them would degrade the dashboard for no gain.

Kestrel's maximum request body drops from 30 MB to 256 KB. Every request this application accepts is
small JSON. This was first attempted as per-endpoint metadata, which minimal APIs expose but the
request pipeline does not honour; the test written to prove it caught that, and the limit moved to
where it actually applies.

### The content policy lives in the page, not in the middleware

A content security policy that denies by default and permits only what drawing a globe requires:
scripts and styles from this origin and Cesium's CDN, workers from `blob:`, WebAssembly for terrain
decoding, imagery from ArcGIS. Everything else is denied, including `base-uri` and `form-action`,
which have no legitimate use here at all.

It is declared in a `meta` element rather than as a response header. That looks like the weaker
choice and is the stronger one: the published dashboard is served by GitHub Pages, which serves files
and sets no headers of its own. A policy expressed only in the API's middleware would have protected
the page served by a locally running process and left the public one — the one people actually visit
— entirely bare.

`SecurityHeaders` then adds exactly what a `meta` element cannot express: `frame-ancestors` is
ignored in `meta`, so framing is refused by header, with `X-Frame-Options` alongside for older
browsers. `nosniff`, `Referrer-Policy: no-referrer`, and a `Permissions-Policy` refusing geolocation,
camera, microphone, and payment join it there.

## Consequences

- A burst of submissions from one client is refused with `429` and a `Retry-After`, a full queue
  answers `503` instead of holding the request, and an oversized submission is refused with the
  reason stated. All three are tested against the real API host.
- The adapters are unaffected. They still block on a full queue, which is what keeps a fast feed from
  overrunning a slow pipeline.
- The rate limiter partitions on the connection's remote address. Behind a reverse proxy that is the
  proxy's address, collapsing every caller into one partition; a proxied deployment needs
  forwarded-header processing before this control means what it says. No supported deployment
  currently has a proxy in front of it, and recording the condition is preferable to configuring for
  a topology that does not exist.
- The body-size limit sits on Kestrel, which `TestServer` bypasses, so it is not covered by the
  integration tests. The test that would have covered it instead asserts what is verifiable: an
  oversized submission is refused, with the limit named in the response.
- The content policy was verified in a real browser rather than reasoned about. It is enforced — an
  injected inline script is refused and never executes — it produces no violations during a normal
  load, Cesium initialises, and ArcGIS imagery still returns `200` rather than silently falling back
  to the bundled offline texture. That last check is the one that mattered: a policy that quietly
  downgraded the globe would have been worse than no policy, and it is precisely what a "no console
  errors" check would have missed.
- The policy constrains where scripts may come from, not which bytes arrive. Subresource integrity
  would add that, at the cost of breaking the page whenever the CDN re-serves the file differently.
  For a dashboard that already degrades gracefully, that trade was declined and recorded rather than
  silently skipped.
- Still no authentication, and the specification's permission to skip it now rests on something. The
  open write path cannot exhaust the process, and the honest summary is that the limits are what make
  the absence of accounts a design choice rather than an omission.
