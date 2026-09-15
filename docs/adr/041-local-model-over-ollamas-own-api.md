# ADR 041: Reach Ollama through its own API, and make a local model the development default

- Status: Accepted
- Date: 2026-09-15
- Narrows [ADR 013](013-deterministic-provider-by-default.md). The deterministic stand-in remains the
  default for a clone with nothing installed; it stops being the default for *this* project's
  development, because a machine with Ollama on it can do better.
- Makes [ADR 039](039-translation-is-recorded-not-inferred.md) actually produce translations. That
  ADR built the machinery and the honest labelling; with no translating provider it could only ever
  report that nothing had been translated.

## Context

ADR 039 made the pipeline record translations properly and made the dashboard admit when none had
happened. What it could not do is translate anything, because the configured provider was the
deterministic stand-in, which matches keywords and identifies scripts and cannot read Arabic.

The published page therefore said, truthfully and uselessly, that 127 of 236 reports were in a
language it had not translated. The honest label was an improvement on the false one it replaced. It
was not the thing anybody wanted.

The obvious fix was a hosted model and an API key. That framing was wrong, and it took too long to
notice: **Ollama was already installed on the development machine, `AiProviderKind.Ollama` was
already in `ChatClientFactory`, and the Ollama path requires no credential at all.** The blocker that
was repeatedly described as "needs a credential" did not exist.

### Why the existing Ollama path was not enough

`ChatClientFactory` reached Ollama by pointing the OpenAI client at the daemon's OpenAI-compatible
shim — sensible, and it avoided a second client library. It is also unusable for this workload with a
current model.

Qwen 3.5 reasons before answering, and on a short classification-and-translation task the reasoning
*is* the cost. Measured against one Arabic report on an RTX 2060:

| Path | Wall clock | Completion tokens | `language` returned |
| --- | --- | --- | --- |
| OpenAI shim, thinking on | 27 s | 1,484 | `"Arabic"` |
| OpenAI shim, `think: false` sent | 47 s | 2,660 | `"Arabic"` |
| OpenAI shim, `/no_think` in prompt | 71 s | 3,966 | *(no answer at all)* |
| Native `/api/chat`, `think: false` | **2.5 s** | **80** | `"ar"` |

Three things follow. The shim cannot turn thinking off. It does not reject the attempt either — it
accepts `think` and ignores it, which is worse than refusing, because the caller has no way to tell.
And the fast path is not merely faster: it returned a correct BCP-47 tag where the deliberating runs
returned a language *name*, which the contract does not ask for and the validator would have
mangled into `"arabic"`.

Over a 259-observation run that is two hours against nine minutes.

## Decision

### A native client for Ollama

`OllamaChatClient` talks to `/api/chat` directly. It sends `think: false` by default, passes the
contract's own JSON schema as Ollama's `format` field, and maps the existing generation bounds onto
`num_predict`, `seed` and `temperature`.

The schema is passed through rather than restated. Two copies of one contract is how they come to
disagree, and the coordinate prohibition of [ADR 005](005-location-resolution.md) is structural — it
has to survive the trip, and a test asserts that it does.

`Ai:EnableThinking` turns the reasoning pass back on for a model whose deliberation is worth the
tokens. Off by default, on the measurement above.

Nothing about the trust boundary changes. The response is validated by the same
`EnrichmentPayloadValidator` as every other provider: a local process is not a trusted one, it is
merely a nearby one.

### A plain HTTP client, not the screened one

The OSINT adapters connect through `PublicInternetConnector`, which refuses to resolve to a private
address — that is the fix for a redirect walking into an internal network, and it is correct there.
A daemon on `localhost` is exactly what that connector exists to block, so this client gets a plain
one. Nothing on it leaves the machine.

### Ollama is the development default; Mock remains the shipped default

`appsettings.Development.json` selects Ollama, so `dotnet run` on a developer machine translates.
`appsettings.json` still selects Mock, so a clone with nothing installed still starts, still ingests,
still deduplicates, locates and correlates, and still needs no credential — which is ADR 013's
guarantee and is untouched.

A missing daemon degrades rather than breaks: the enrichment service absorbs provider failures by
contract, the deterministic keyword classification stands, and the observation is processed. The
failure mode of "Ollama is not running" is the behaviour of the day before this ADR.

## Consequences

**Foreign-language reports are now actually readable.** Verified end to end against the committed
collection bundles: 36 observations, 9 machine-translated from Russian, French and Arabic, 22
already English, 5 untranslated — and all five of those are duplicates, which are deduplicated
before enrichment and so never reach a model at all. Sample output, real records:

> `Кремль считает хорошей инициативой идею Трампа об энергетическом перемирии`
> → *Moscow Endorses Trump's Energy Truce Idea*

**A second client library is not added, but a second client is.** The cost is roughly 200 lines and
a set of tests. The alternative — pulling a non-reasoning model so the shim becomes adequate — was
rejected because it makes the model choice load-bearing and invisible: someone configuring a
reasoning model later would get a twenty-fold slowdown and nothing to explain it.

**The published deploy is not fixed by this.** A GitHub-hosted runner has four CPU cores and no GPU,
and this repository has no self-hosted runner. Local enrichment of a full run takes about nine
minutes here and would take substantially longer there, on every push. That is a deployment
question, recorded as open rather than answered: the page continues to label untranslated reports
honestly, which is what [ADR 039](039-translation-is-recorded-not-inferred.md) is for.
