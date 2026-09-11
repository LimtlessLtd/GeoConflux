# ADR 018: Report a heuristic activity score, and report what it is made of

- Status: Accepted
- Date: 2026-09-11

## Context

The specification asks for a "Geopolitical Activity Score" and requires its calculation to be
documented. It also forbids inventing metrics or presenting a heuristic as objective truth.

Those two requirements pull against each other in a specific way. A single 0–100 number attached to
the words "geopolitical activity" is exactly the kind of figure a reader quotes without its caveats.
Once it is screenshotted, the number travels and the disclaimer does not. So the risk here is not
that the arithmetic is wrong — it is that a correct calculation over soft inputs gets read as a
measurement of the world.

The inputs are genuinely soft. Severity comes from a keyword classifier or from a language model.
Confidence is a heuristic in the first case and a self-report in the second. Observation count
measures how many sources this system happened to ingest, not how many exist. None of that makes a
summary worthless, but all of it makes an unqualified summary misleading.

## Decision

Compute one bounded score per window, and make it structurally impossible to receive the number
without receiving its definition.

Each incident contributes the product of four factors:

| Factor | Form | Why |
| --- | --- | --- |
| Severity | Critical 8, High 4, Medium 2, Low 1, Unknown 0.5 | Doubling per step encodes a claim that is statable and arguable rather than hidden. Unknown is non-zero because an unclassified report is still a report. |
| Recency | `0.5^(age / (window / 4))` | Half-life is a quarter of the window, so "recent" means the same thing at every zoom level. The oldest incident in a window retains 1/16 of its weight. |
| Corroboration | `min(3, 1 + 0.5 × log2(evidence))` | The second source is worth far more than the tenth. The cap stops one syndicated wire story outweighing a genuinely multi-sourced event. |
| Confidence | `0.4 + 0.6 × confidence` | Scales the contribution without erasing it. A doubtful classification still describes a report that arrived. |

The sum is divided by the window length in days, giving a rate — this is the specification's fifth
component, event frequency. The rate is then mapped onto 0–100 by `100 × (1 − exp(−R / 6))`.

Four consequences of that shape are deliberate:

- **Bounded by construction, not by clamping.** No volume of activity can exceed 100, and nothing is
  truncated to make that true.
- **Rate-normalised, so windows are comparable.** Without the division, a 90-day view of the same
  activity would always score higher than a 24-hour view, and switching tabs would look like an
  escalation. A unit test asserts this property directly.
- **Monotone in every factor.** More severe, more recent, better corroborated, or more confidently
  classified always raises the score. Each direction has its own test.
- **One arbitrary constant, reported alongside its own input.** The saturation rate of 6 weighted
  incidents per day is a presentation choice, not a measurement. The raw rate is therefore returned
  next to the score, so a reader who distrusts the curve can use the number the curve was applied to.

The response carries the formula, a per-factor breakdown with plain-language descriptions, the raw
weighted total and rate, the number of incidents scored, and a notice stating what the score is not.
The dashboard renders all of it in the same card as the number. Neither the API nor the UI has a path
that emits the value alone.

The score is computed over a capped sample (5,000 incidents per window). When the cap binds, the
response says so and the card shows a warning, because a score over part of a window is a different
number.

## Calibration

The saturation constant was chosen by running the real pipeline over the recorded replay stream and
reading the result, not by picking a round number. That run produced 7 incidents from 11
observations, and the four windows scored:

| Window | Weighted total | Rate (per day) | Score | Band |
| --- | ---: | ---: | ---: | --- |
| 24h | 6.71 | 6.71 | 67.3 | elevated |
| 7d | 8.36 | 1.19 | 18.1 | moderate |
| 30d | 8.62 | 0.29 | 4.7 | quiet |
| 90d | 8.68 | 0.10 | 1.6 | quiet |

That spread is the point of the exercise: the constant is set so real volumes land across the range
rather than pinned at either end. The decline across windows is correct and worth reading carefully —
the replay stream is a single burst of activity, so its rate genuinely is low when measured over
ninety days. The score is behaving as specified; it is not decaying because the data is stale.

A deployment ingesting a different volume would need a different constant, and scores from two
deployments that chose differently are not comparable. This is stated in the code and in the UI.

## Consequences

- The number is defensible because it can be taken apart. Anyone can disagree with the severity
  weights or the half-life and recompute.
- The score measures this system's intake, not the world. A quiet score may mean a quiet period, or
  it may mean no adapter was configured and nothing arrived. The score cannot distinguish them, and
  says so.
- Bands (`quiet`, `moderate`, `elevated`, `high`) accompany the value so a reader comparing 41 with
  46 does not read a difference into them the heuristic cannot support.
- Adding a factor or changing a weight changes every published score. The constants are named,
  documented, and covered by property tests rather than by expected values, so a deliberate change is
  a one-line edit and an accidental one fails the suite.
