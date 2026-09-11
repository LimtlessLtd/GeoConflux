# AI evaluation fixtures

`fixtures.json` holds labelled cases used by `Geopolitics.AiEvaluationTests`.
`RESULTS.md` is written by that test run and is not edited by hand.

## What the harness does

It runs the real `ChatClientEnrichmentService` — the same prompt, the same schema, the same
validator the pipeline uses — over every fixture, and scores the result against the labels.

It evaluates whichever provider is configured. With none configured it uses the deterministic
in-process stand-in, so `dotnet test` measures the **offline baseline** with no credentials and no
network. To measure a real model instead:

```powershell
$env:GEOCONFLUX_EVAL_PROVIDER = "Ollama"   # or OpenAI, AzureOpenAI
$env:GEOCONFLUX_EVAL_MODEL    = "llama3.2"
dotnet test tests/Geopolitics.AiEvaluationTests
```

Credentials come from `GEOCONFLUX_EVAL_API_KEY` and are never committed.

## What is measured

| Task | Metric |
| --- | --- |
| Event classification | per-class precision / recall / F1, macro F1, accuracy |
| Severity assessment | per-class precision / recall / F1, macro F1, accuracy |
| Language identification | accuracy |
| Location **naming** | precision / recall / F1 over the named place |
| Entity extraction | precision / recall / F1 over the set of names |
| Structured output | share of cases producing a schema-valid response |
| Latency | median and p95 per enrichment attempt |

Macro F1 rather than micro: the fixture set is small and its classes are uneven, so a micro
average would mostly report how well the common classes did. Macro weights each class equally and
therefore exposes a category the system never gets right.

Location scoring covers the **name**, not coordinates. Under [ADR 005](../../../docs/adr/005-location-resolution.md)
a model never produces a position, so there is no coordinate accuracy to measure here. A separate
test asserts that no enrichment output contains a latitude or longitude at all.

## Limitations, stated plainly

- **The texts are synthetic.** They were written for this repository. They describe no real event,
  person, vessel, or organisation. Any name appearing in them is invented.
- **The labels are author-assigned.** They are one engineer's reading, not expert adjudication and
  not inter-annotator consensus. Severity in particular is a judgement call that reasonable people
  would make differently.
- **Sixteen cases is far too few** to support a confidence interval. No figure here should be
  quoted as a measurement of geopolitical classification ability.
- **The set is skewed towards maritime and conflict reporting** because that is what the demo
  gazetteer covers. Performance on other subject matter is unmeasured, not good.
- **The baseline is English-bound.** The offline stand-in reads Latin script and matches keywords,
  so its recall on the Arabic, Russian, and Chinese fixtures is poor. That is the gap a real model
  is expected to close, and it is why those fixtures are labelled with the place the text actually
  names rather than with what the baseline happens to be able to find.
- **A live run is not reproducible.** Temperature is 0 and a seed is sent where the provider
  supports it, which reduces variation without eliminating it. Only the stand-in's figures are
  stable enough for CI to enforce.

## What the thresholds are for

The asserted floors are regression guards derived from a measured run and rounded down. They fail
the build when a prompt, classifier, or gazetteer change makes results worse. They are not quality
targets, and clearing them is not evidence that the system classifies well.
