# Severity model dataset

`dataset.json` is the labelled corpus behind the classical severity model and its evaluation.

## What this is, and what it is not

Every report in this file is **invented for this repository**. The vessels, units, companies, and
operations named in them do not exist, and none of the events described happened. Place names are
real because the geography has to be plausible, but nothing here describes a real event at any of
them.

The labels are **author-assigned against the rubric below**. They were not adjudicated by a domain
expert, and they were not produced by consensus between several labellers. What the model learns is
therefore the author's reading of that rubric, and what the evaluation measures is how well a small
linear model recovers that reading from 100-odd examples.

That is a real machine-learning result and it is reported as one. It is not evidence that the model
can assess the severity of a real geopolitical event, and no figure derived from this file should be
read that way.

## Rubric

Labels follow the consequence described in the report, not the emotional register of its wording.
A calmly-worded report of a sinking is High; an alarmed report of a routine transit is Low.

| Label | Criterion |
| --- | --- |
| `CRITICAL` | Mass casualties; use or credible threat of a nuclear, chemical, or biological weapon; a national state of emergency; destruction of critical national infrastructure; direct armed escalation between states. |
| `HIGH` | Deaths; a vessel or aircraft lost, seized with crew aboard, or seriously damaged; hostages taken; a strategic route closed or blocked; compromise of an operational system at critical infrastructure. |
| `MEDIUM` | Injuries without deaths; property or cargo damage; detentions, seizures, or impoundments without harm to persons; temporary disruption to movement or service; protests involving clashes; sanctions imposed or tightened. |
| `LOW` | No casualties and no damage. Routine deployments, exercises, transits, patrols, statements, and monitoring reports. Attempts that failed without consequence. |

Two labelling conventions worth stating, because they are where a reader would otherwise disagree
with the file:

- **An averted event is labelled by what happened, not by what was avoided.** A boarding attempt
  repelled with nobody hurt is `LOW`, even though a successful one would have been `HIGH`.
- **Scale beats category.** A protest is normally `MEDIUM` at most, but a protest that kills dozens
  and triggers a state of emergency is `CRITICAL`. The rubric describes consequences; event type is a
  separate feature the model sees anyway.

## Split

Each case carries its own `split`, `train` or `test`, fixed in the file rather than drawn at
evaluation time. Two reasons:

- A split re-randomised per run would make every reported figure a different measurement, and a
  regression would be indistinguishable from a reshuffle.
- A split chosen by the trainer would let a future change quietly pick a kinder one. Writing it down
  makes moving a case a visible diff.

The test cases are never trained on. `SeverityModelEvaluationTests` asserts this, because a leak
would inflate every figure in `RESULTS.md` while leaving it looking entirely reasonable.

## Features

The model sees the report text, its event type, and four numeric features that come from the
pipeline rather than from the text: how many observations support the incident, the confidence of the
classification that produced it, how many actors were extracted, and whether it could be placed.

Those four are included because they are the part a bag of words cannot see. A single unconfirmed
report and four corroborating ones can use identical wording and still differ in what they justify
concluding.

## Regenerating the results

`RESULTS.md` is written by the evaluation test and should not be edited by hand:

```powershell
dotnet test tests/Geopolitics.AiEvaluationTests
```
