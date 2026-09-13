# ADR 030: Collection reads what a service chooses to serve, and stops there

- Status: Accepted
- Date: 2026-09-13
- Implements the access rules stated in [ADR 025](025-agent-collected-osint.md) and required by
  [ADR 029](029-corroboration-gate.md), which made Tier B collection possible in the first place.

## Context

The corroboration gate removed the reason Tier B could not be collected. What remained was the
question of how, and that is not primarily a parsing problem.

The plan is unusually direct about why it matters, and the sentence is worth keeping: *a portfolio
project that reaches its data by violating a platform's terms has demonstrated the wrong thing, and
no amount of engineering quality elsewhere recovers it.* Every technique that would widen coverage
here — a browser that renders what an API declined to serve, an account that presents an automated
reader as a person, an unlabelled user agent — buys reach at the cost of the only thing this project
is actually claiming.

So the access rules are code rather than a paragraph in a brief. A brief is a document, and a page
being read can argue with a document.

## Decision

### Four rules, in `tools/collect/access.py`

**A missing `robots.txt` permits everything.** RFC 9309 §2.3.1.3. An absent file is not a refusal,
and reading it as one would block hosts that simply never wrote one. This is not hypothetical:
`t.me` answers 404, verified 2026-09-13, so a stricter reading would have excluded the single
highest-value social surface for conflict OSINT on a technicality.

**An unreachable `robots.txt` forbids everything.** The same RFC, and the opposite answer. A 500 or
a timeout means the rules are unknown, and "unknown rules presumably permit what I want" is the
reasoning every badly-behaved crawler uses. The two cases look almost identical from the call site
and mean opposite things, which is exactly why both are asserted.

**A gate is a refusal.** HTTP 401, 402, 403, 407 and 451 are recorded and not retried — not with
different headers, not with a different agent, not logged out through another door. A source behind
a gate is a Tier C entry, not a challenge. X is the live example twice over: the plan recorded
HTTP 402 on an unauthenticated profile request, and this tool independently finds that
`x.com/robots.txt` disallows the profile path. Two separate refusals, both respected.

**Every request identifies itself and waits its turn.** The user agent names the project and links
to it, because an automated reader that will not say what it is has already decided it does not want
to be asked to stop, and a contact link is what turns a rate limit into a conversation. Requests to
one host are spaced by two seconds, raised by a published `Crawl-delay` and never lowered by one — a
host saying "one second is fine" is not a reason to go faster than this project has decided to go.

A crawl-delay above sixty seconds is treated as a refusal rather than obeyed. Waiting that long
inside a run looks like a hang, and recording the host as unreachable is both honest and visible.

### Decisions are pure, and are a CI step

Nothing that *decides* anything touches the network. Whether a path is allowed, how long to wait,
whether a status is a refusal, whether an excerpt fits — each is a function of its inputs, and both
tools carry an offline `--self-test` that CI runs on every push.

This matters more than it looks. The rules above are the kind that are stated in a README, believed,
and quietly untrue a year later. Forty-five assertions that run on every commit are a different sort
of claim from a paragraph.

### The collector may not name a place it did not read

Place names are found by matching the text against the committed gazetteer extract, not by
recognising entities. "The place names appearing verbatim in the text" is a substring question
against a known lexicon, and answering it that way keeps the collector inside the remit ADR 025 set:
it reports what it read, and the pipeline's own gazetteer decides what that means.

The short-name rule from `Gazetteer.cs` is duplicated here, deliberately and with a comment pointing
at the original. The extract contains villages genuinely named Sad, Rama, Gora and Bile, and aliases
including Luck and Mare; a collector recording "Luck" as a place name every time a post said "a
stroke of luck" would be feeding the pipeline the same regression through a new door. Duplication
across two languages is a real cost and it is smaller than that.

### A repost is not a second witness

Bluesky reposts and Mastodon boosts are skipped rather than recorded as the channel's own items. A
repost is one claim travelling, and collecting it would let an account corroborate itself by
amplification — which is precisely the gate ADR 029 built, defeated at the collection step instead
of the pipeline one.

### Diversity caps are applied by the tool, and what they dropped is printed

The brief states a cap per channel, per platform, and per run. The tool enforces all three and names
every item it dropped and why. A run that quietly discarded half of what it found would read as a
thin day rather than as a cap doing its job.

## Consequences

- **Telegram, Bluesky and Mastodon are all readable with no credential**, confirmed against the live
  services on 2026-09-13: `t.me/s/<channel>` serves full post text and timestamps with no
  JavaScript, `public.api.bsky.app` explicitly permits crawling the public API in its `robots.txt`,
  and `mastodon.social` disallows only `/media_proxy/`, `/interact/` and one instance endpoint.
- **Bluesky is collected by curated account list, not by search**, because `searchPosts` answers 403.
  That is a limitation of what may be read, not a design preference, and it is why a brief names
  channels.
- **`mastodon.social` names GPTBot in a `Disallow: /` of its own.** GeoConflux is matched by `*` and
  is not that: it quotes bounded excerpts with citations rather than harvesting a corpus. The rule is
  honoured exactly as written, and the distinction is recorded here rather than relied on silently.
- **The tools are not in the .NET or Node suites**, like the gazetteer extractor before them. What
  keeps them honest is that the artefact they produce — a bundle — is linted on every build by the
  same parser that reads it at runtime, and that their own decisions are gated by a CI step.
- **A run never invokes the application and the application never invokes a run.** That separation is
  what keeps the system deterministic, credential-free and reproducible from the repository, and it
  is the reason a bundle can be reviewed in a diff.
