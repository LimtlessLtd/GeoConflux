# Security review

Date: 2026-09-12. Scope: the whole repository at that date — application code, the dashboard, the
container image, and both CI workflows.

This is the review Sprint 6 asks for, and it is a review rather than a summary: every finding below
was confirmed against running code before it was written down, and every fix was confirmed by a test
that fails without it. Things that turned out not to be problems are recorded too, because a review
listing only problems gives no way to tell what was actually examined.

## What this system's attack surface actually is

Three things distinguish it from a generic CRUD application, and they are where the review
concentrated.

1. **It fetches from hosts it does not control.** Three adapters poll external providers, and the
   published dashboard is built by polling four public news feeds on every deploy.
2. **It renders text written by strangers.** Feed items, and anything posted to the submission
   endpoint, both end up on screen beside this system's own assessments.
3. **It has no authentication, by design.** The specification permits this and the project does not
   need an identity system. That choice is only defensible if the one open write path cannot be used
   to exhaust the process.

## Findings

### 1. High — a provider credential was written to the logs on every poll

**Where.** `NasaFirmsEventSource.FetchAsync`, through the HTTP client logging that
`IHttpClientFactory` installs.

**What was wrong.** The FIRMS area API takes its map key as a *path segment*. That is their API
design, not a choice available here. The runtime's default HTTP logging redacts query strings and
header values but writes the request path verbatim, at `Information` level — the level this
application ships. A live FIRMS deployment therefore published its map key into every log sink on
every poll, once per attempt, retries included.

The code asserted the opposite. A comment in `NasaFirmsEventSource` read "It is escaped and never
logged; the log messages below name the dataset and area only." That was true of the class's own
messages and false of the pipeline they run in — the more dangerous kind of wrong, because it tells a
reviewer to stop looking.

ACLED, which puts its key and registered email in the query string, was not leaking. But only because
of a runtime default that a deployment can switch off with an environment variable. Protection by
coincidence, not by design.

**Why it matters.** Logs travel. They reach aggregators, get attached to tickets, and get pasted into
chat. A credential in an application log has to be assumed compromised.

**Fix.** `ProviderSecretRedactor` is told what the configured credentials are and masks them wherever
they appear, in literal and URL-escaped form. `RedactingHttpClientLogger` replaces the runtime's
logger for these clients, drops query values wholesale, and renders exceptions to redacted text
rather than handing them to the logger as objects. The redactor matches configured values instead of
guessing at key-shaped strings, which would both miss real credentials and mangle innocent path
segments such as a dataset name.

**Verified by.** `ProviderSecurityTests.TheFirmsMapKeyNeverReachesTheLogs` and
`.TheAcledKeyAndRegisteredEmailNeverReachTheLogs`. Both capture every line the container emits at
`Trace`, and both assert the credential *was* sent — so they prove it was redacted on the way to the
log rather than that the request never happened. The first failed before the fix.

### 2. Medium-High — a feed could redirect this process into a private network

**Where.** All three adapters, through automatic redirect handling.

**What was wrong.** Feed URLs are configured by whoever deploys this, so the initial target is
trustworthy. The *response* is not. A publisher answering `302 Location:
http://169.254.169.254/latest/meta-data/` would have had this process fetch a cloud instance's
credentials on its behalf. One answering with `http://localhost:8080/api/...` would have turned an
outbound poll into a request against this application's own API. Redirects were followed
automatically, to any host, up to the framework default of 50 hops. Nothing checked that a configured
feed URL was even `http`.

**Why it matters.** This is the textbook SSRF shape, and the cloud metadata endpoint is why it is
worth taking seriously: on most hosted infrastructure it answers with no credential at all. The
specification asks for this guard explicitly.

**Fix.** Three parts. `OutboundAddressPolicy` decides which addresses a poll may reach — loopback,
link-local, RFC 1918, carrier-grade NAT, IPv6 unique-local and link-local, multicast, and reserved
space are refused. `PublicInternetConnector` enforces it at connect time through
`SocketsHttpHandler.ConnectCallback`, the only place that sees the original request, every redirect
hop, and the address a name *actually* resolved to. `ProviderOptionsValidator` rejects feed URLs that
are not absolute `http` or `https` at startup rather than at poll time. The redirect cap drops from
50 to 3.

Filtering on the resolved address rather than the hostname is the part that matters. A hostname check
catches only the naive case: a name under the redirecting party's control can resolve to whatever
they choose, so a host that looks external and points at `127.0.0.1` passes a name check and fails
this one.

**Verified by.** `ProviderSecurityTests` — twenty cases across the policy, including boundary
addresses that must *not* be blocked, four rejected URL schemes, and
`AFeedPointedAtLoopbackIsRefusedByTheRealHttpStack`, which runs against the production transport
rather than a stub, so it proves the policy is actually consulted.

**Accepted limitation.** There is no configuration switch permitting an internal feed mirror. A
deployment needing one would have to add an allow-list. That is deliberately not provided: an unused
escape hatch in a security control is a liability.

### 3. Medium — the open submission endpoint could park requests indefinitely

**Where.** `POST /api/observations`, through `ChannelObservationBuffer.EnqueueAsync`.

**What was wrong.** The queue is bounded and uses `BoundedChannelFullMode.Wait`, so producers block
when it is full. That is correct for a polling adapter: a feed that cannot be enqueued should slow
down rather than be dropped. It is wrong for an HTTP request, because a request that waits is a
connection held open, and nothing limited how many an unauthenticated caller could open. A full queue
was therefore expressible as unbounded held connections. Separately, a large body was read and parsed
in full before the 20,000-character content check rejected it.

**Fix.** The wait is bounded at the endpoint — five seconds, then `503` with an honest explanation,
rather than a `500` that would blame the caller for the server's state. The bound belongs at the
endpoint and not in the queue, because the adapters *should* keep blocking. A fixed-window rate
limiter partitioned by remote address applies to that endpoint alone, rejecting immediately rather
than queueing, and returning `Retry-After`. Kestrel's maximum request body drops from 30 MB to
256 KB.

**Verified by.** `SubmissionProtectionTests` — a burst is refused with `Retry-After` present, a full
queue answers `503` instead of hanging, and an oversized submission is refused with the reason
stated.

**Known gaps, stated rather than hidden.** The rate limiter partitions on
`HttpContext.Connection.RemoteIpAddress`. Behind a reverse proxy that is the proxy's address, which
would collapse every caller into one partition; a proxied deployment needs forwarded-header
processing for this control to keep meaning what it says. No supported deployment currently has a
proxy in front of it. The body-size limit is set on Kestrel, which `TestServer` bypasses, so it is
not covered by the integration tests — the per-endpoint metadata that minimal APIs expose for this
was tried first and found not to be honoured by the request pipeline, which is why the limit sits
where it does.

### 4. Medium — nothing stood behind the dashboard's escaping

**Where.** `wwwroot/index.html`, `wwwroot/app.js`, and the API's response headers.

**What was wrong.** No injection was found. Every render path in `app.js` passes untrusted text
through `escapeHtml`, which escapes `& < > " '`, and no feed-supplied value reaches an `href`, `src`,
or any script sink. The problem was that this correctness was the *only* thing between a stranger's
headline and script execution. There were no security response headers, and nothing constrained the
CDN-delivered globe library.

**Why it matters.** Escaping every site correctly is a property that must hold at every render call,
forever, including the next one somebody adds. A content policy holds regardless.

**Fix.** A content security policy that denies by default and allows only what drawing a globe
requires. It is declared in a `meta` element rather than as a response header because the published
dashboard is served by GitHub Pages, which serves files and sets no headers — a policy living only in
the API's middleware would have protected the locally hosted page and left the public one bare.
`SecurityHeaders` adds what a `meta` element cannot express: `frame-ancestors 'none'` and
`X-Frame-Options`, plus `nosniff`, `Referrer-Policy: no-referrer`, and a `Permissions-Policy`
refusing capabilities this dashboard never asks for.

**Verified by.** A headless Chrome run against the real page, loaded twice — once with the policy and
once without — and compared on failed requests. That comparison is the check that matters, and it is
stronger than it started out: the first draft of this policy left Cesium's CDN out of `worker-src`,
which stopped two worker scripts loading and logged no violation at all, because a worker blocked at
construction does not report one the owning page can see. A violations-only check passed it. The
comparison did not.

With that corrected, the two runs are identical, and the rest holds: the policy is enforced (an
injected inline script is refused and never executes), it produces zero violations during a normal
load, Cesium initialises, and ArcGIS imagery returns `200` rather than silently falling back to the
offline texture. A policy that quietly downgraded the globe would have been worse than none.

### 5. Low — CI actions were pinned to mutable tags

**What was wrong.** `actions/checkout@v4` and three others resolved to whatever those repositories
decided `v4` meant on the day a run happened. An action runs with access to the workflow's token, and
the Pages workflow's token can publish the site.

**Fix.** All four pinned to commit SHAs, each with a trailing comment naming the release it was, since
a bare hash is unreviewable. `ci.yml` now also declares `permissions: contents: read` rather than
inheriting the repository default.

## Checked and found sound

- **Prompt injection.** Observation text is wrapped in explicit delimiters and restated as data, the
  model is told it may never output coordinates, the response is schema-validated, and location is
  resolved deterministically from a gazetteer. Mitigation plus validation, which is the right shape —
  the delimiters are not trusted to work on their own.
- **SQL injection.** No raw SQL anywhere. Every query is EF LINQ.
- **Secrets in the repository.** None tracked, none in history. `.gitignore` covers `*.db`,
  `secrets.json`, and local settings. Credentials are read from configuration, ship empty, and no
  adapter starts without one.
- **CORS.** No policy is registered, so the browser's same-origin default applies. Correct: nothing
  needs cross-origin access.
- **SignalR.** The hub has no server-callable methods at all. It is an outbound projection, not an
  entry point into the pipeline.
- **Container.** Runs as the image's non-root user, with the data directory created and handed over
  before the switch.
- **Queue growth.** Bounded, with the fullness behaviour chosen deliberately per producer.
- **Dependencies.** See [the dependency review](dependency-review.md).

## Not done, and why

- **No authentication.** The specification permits it and the project does not need one. Finding 3 is
  what pays for that choice.
- **No HSTS or HTTPS redirection.** TLS termination is the deployment's job; the container listens on
  plain HTTP behind whatever fronts it. Emitting HSTS from a process that does not itself terminate
  TLS would be a claim the process cannot keep.
- **No subresource integrity on the globe library.** `script-src` restricts *where* a script may come
  from, not which bytes arrive. An `integrity` hash would add that, but would also break the page the
  day the CDN re-serves the file differently. For a dashboard whose failure mode is already a
  graceful fallback, that trade is not obviously worth taking. Recorded as a known gap rather than
  silently omitted.
