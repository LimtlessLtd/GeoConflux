# ADR 021: A poll may reach only the public internet, and may write down only what is not a secret

- Status: Accepted
- Date: 2026-09-12
- Builds on [ADR 015](015-live-provider-ingestion.md), which established the adapters and their
  resilience but treated the network they reach as a given.

## Context

ADR 015 decided how this system talks to external providers: adapter per source, shared resilience
pipeline, dormant unless explicitly configured. What it did not decide is what those adapters are
permitted to *reach*, or what the act of reaching leaves behind in the logs. The Sprint 6 security
review found a real defect in each.

**A credential was being published.** The FIRMS area API takes its map key as a URL path segment.
The runtime's default `IHttpClientFactory` logging redacts query strings and header values but writes
the path verbatim, at `Information` — the level this application ships at. Every live FIRMS poll
therefore wrote its map key into every log sink, once per attempt including retries. ACLED, whose key
sits in the query string, was not leaking, but only because of a runtime default a deployment can
disable with an environment variable.

Worse than the defect was the comment above it, which said the key was "escaped and never logged".
That was true of the class's own log statements and false of the logging pipeline they ran inside. A
confident, wrong comment is the most expensive kind, because it tells the next reviewer not to check.

**A feed could choose where this process connects.** A feed URL is configured by whoever deploys
this, so the first request goes somewhere trusted. The response does not have to. Redirects were
followed automatically, to any host, up to the framework default of fifty hops. A publisher — or
anyone who has taken one over — answering `302 Location: http://169.254.169.254/latest/meta-data/`
would have had this process fetch a cloud instance's credentials for them. One answering with a
loopback address would have turned an outbound poll into a request against this application's own
API.

## Decision

### Outbound connections are screened at the socket, not at the URL

`OutboundAddressPolicy` states which addresses an OSINT poll may reach. Loopback, link-local
(including the cloud metadata range), RFC 1918 private space, carrier-grade NAT, IPv6 unique-local
and link-local, multicast, and reserved blocks are all refused. `PublicInternetConnector` enforces it
through `SocketsHttpHandler.ConnectCallback`.

The placement is the decision. Three alternatives were available and each is weaker:

- **Validating the configured URL only.** Covers the initial request and nothing else. The redirect
  is the untrusted part.
- **Disabling redirects entirely.** Would break real feeds, which move and which redirect `http` to
  `https` routinely.
- **Validating the hostname of each redirect.** Catches only the naive attempt. A name under the
  redirecting party's control resolves to whatever they choose, so a host that looks perfectly
  external and points at `127.0.0.1` passes a name check.

The connect callback is the one place that sees the original request, every redirect hop, and the
address a name *actually* resolved to. One rule there covers all three, instead of three rules that
have to be kept in agreement.

No override is provided. A deployment wanting to poll an internal mirror would have to add an
allow-list, and that is deliberate: an unused escape hatch in a security control is a liability, and
this system's adapters exist to read public feeds.

### Outbound logging is replaced, not supplemented

The runtime's HTTP logger is removed for the provider clients and `RedactingHttpClientLogger` takes
its place. It drops query values wholesale — preserving the runtime's own default now that the
runtime's logger is gone — and masks any configured credential wherever it appears.

`ProviderSecretRedactor` is *told* what the secrets are rather than inferring them. A heuristic
looking for key-shaped strings would do both things wrong at once: miss a credential that does not
look like one, and mangle an innocent path segment like a dataset name. Matching the values the
deployment actually configured, in literal and URL-escaped form, is exact.

Exceptions are rendered to redacted text rather than handed to the logger as objects. A socket error,
a URI format error, or a provider echoing the request back all carry the URL in their message often
enough that logging the object directly would reopen the hole.

### Feed URLs are validated at startup

`ProviderOptionsValidator` rejects a feed URL that is not an absolute `http` or `https` URL, and the
same for each provider base address, when the host starts rather than when a poll fails. A misconfigured
feed is a deployment error, and a deployment error should stop the deployment — discovering it later
means a source that silently produced nothing, which is the failure hardest to notice from the
dashboard.

## Consequences

- A FIRMS deployment no longer publishes its map key. Two tests capture every line the container
  emits at `Trace` and assert the credential *was* sent but does not appear, so they prove redaction
  rather than absence of a request. One of them failed before this change.
- The provider clients log less than the framework did — method, host, path shape, status, elapsed
  time — and what they log is safe by construction rather than by the framework's defaults happening
  to line up with where a given API puts its credential.
- A redirect into private address space fails the connection with a stated reason instead of
  succeeding. `AFeedPointedAtLoopbackIsRefusedByTheRealHttpStack` exercises the production transport
  rather than a stub, so it fails if the handler registration is ever reordered such that the policy
  stops being consulted.
- The redirect cap drops from fifty to three. A feed needing more than a couple of hops is
  misconfigured, and a long chain is a cheap way to occupy a poll slot.
- DNS rebinding between the check and the connection is closed, because there is no gap: the address
  checked is the address connected to.
- The policy blocks private space unconditionally. A deployment behind a corporate proxy on RFC 1918
  space cannot use these adapters without adding an allow-list. That is the intended trade and it is
  written down here so it is revisited deliberately rather than discovered.
- Boundary addresses are tested in both directions. A policy that blocked `172.32.0.0` or
  `100.63.255.255` would be quietly refusing real feeds, and the adapter would report it as a bad day
  upstream — a false positive here is close to invisible, which is why the tests assert what must
  still be reachable as carefully as what must not.
