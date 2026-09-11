# ADR 020: Train the severity model from the corpus, and never let it decide

- Status: Accepted
- Date: 2026-09-11
- Supersedes the deferral recorded in [ADR 009](009-ml-model.md), which said a classical severity
  model would arrive in Sprint 5 and left the details open.

## Context

ADR 009 established *why* a conventional model belongs here: a language model's severity assessment
is hard to attribute, hard to reproduce, and impossible to score against a baseline that shares none
of its machinery. A small supervised model trained on a fixed labelled corpus can be scored against
held-out labels and against the language model on the same cases.

What it left open is everything that determines whether the result means anything: where the model
lives, how it is versioned, what it is allowed to change, and how a reader is stopped from quoting
its output as a fact about the world.

That last point is the real risk. The model returns a severity label and a probability. Both look
authoritative. Neither is: the corpus is synthetic, the labels are author-assigned, and the
probability is a calibration against a training distribution of 95 rows.

## Decision

### The corpus is the artefact; the model is derived

The model is trained in-process, at first use, from a corpus embedded in the Infrastructure assembly.
No `.zip` is committed.

The alternative — train offline, commit the binary — is the conventional choice and was rejected
deliberately. A committed model is an opaque artefact nobody can diff, reproduce, or verify came from
the dataset sitting beside it, and the version it claims is whatever the last person to regenerate it
typed. Training from the corpus makes every input to the model a readable file under version control,
and makes "does this model match this dataset" true by construction rather than by convention.

The cost is a few hundred milliseconds once per process, paid lazily by whoever predicts first —
which in every host that runs the pipeline is the background processor, not a request thread.

### Everything about the fit is pinned

Fixed seed, one training thread, and a key ordering taken from the label values rather than from the
order rows arrive in. Two runs over the same corpus produce the same model, which is what makes the
evaluation figures a measurement rather than a sample; a test asserts it. Without the key ordering,
reordering two lines in the dataset file would renumber every class.

The version string is `sdca-maximum-entropy/features-1/dataset-1` — trainer, feature-set version,
dataset version — and is persisted with every stored prediction. The feature-set version exists so
that adding or redefining a feature cannot silently re-attribute old predictions to a model that saw
something different.

### The features are a contract in the Application layer

`SeverityFeatures` is defined next to the interface, not inside the ML implementation, and both the
trainer and the pipeline build it through the same factory. Feature drift between training and
inference is the classic way a model quietly stops meaning anything, and this makes it a change to a
signature that three call sites compile against.

Four of the features — source count, classification confidence, entity count, and whether the report
could be placed — are there because a bag of words cannot see them. One unconfirmed report and four
corroborating ones can use identical wording and still differ in what they justify concluding.

### It is a second opinion, structurally

The pipeline records what the model predicted and whether that agreed with the severity already in
force. It cannot change one. Three things enforce this rather than describe it:

- The domain method is `RecordModelSeverity`, and it writes to `ModelSeverity`, never to `Severity`.
- A missing prediction is `null`, not a default. "No second opinion was taken" and "the model
  assessed this as Unknown" are different facts and must not collapse into one value.
- Every failure in the stage is swallowed. A disabled model, an unfitted one, a slow one, and one
  that throws all produce the same outcome: no prediction, and the observation processes normally. A
  second opinion that can fail an observation is not a second opinion, it is a new dependency.

### The evaluation compares, and asserts the comparison

The model and the enrichment path are scored on the same 45 held-out cases, resolved through the same
provider configuration, against the same labels. Three figures come out: each system's accuracy
against the labels, and how often the two agree with each other.

Agreement is reported but never presented as accuracy — both can agree and both be wrong. The one
claim the evaluation makes for the model is the margin between the two accuracy figures, and that
margin is asserted: if the model stops beating the deterministic classifier already in the pipeline,
the build fails. At that point the honest response is to remove it, and this makes that visible
instead of leaving a model in place because it is there.

The split is written into the corpus file rather than drawn at evaluation time, and the test asserts
the two sets are disjoint by id *and* by text. A leak would raise every figure in the report while
leaving it looking entirely reasonable.

## Measured results

From the run that produced `tests/data/severity-model/RESULTS.md`:

| Measure | Severity model | Enrichment provider (deterministic stand-in) |
| --- | ---: | ---: |
| Accuracy against labels | 0.56 | 0.38 |
| Macro F1 against labels | 0.51 | 0.32 |
| Correct of 45 | 25 | 17 |

The two agreed with each other on 18 of 45 cases. The model is clearly better than the keyword
baseline and clearly not good: its recall on `CRITICAL` is 0.25 and on `HIGH` is 0.30, because with
95 training rows a regularised linear model hedges toward the middle classes. That is reported rather
than tuned away, since tuning it on a 45-case holdout would be fitting the holdout.

Regression floors are set at 0.48 accuracy and 0.42 macro F1 — above what the holdout gives away for
free, since the majority class alone scores 0.31 — and low enough that a last-decimal
floating-point difference between platforms cannot fail CI.

## Consequences

- The pipeline now records, on real traffic, where a trained model and the enrichment path disagree.
  That is a comparison a labelled corpus cannot give, and it accumulates without anyone curating it.
- ML.NET is a substantial dependency for one small model. It is justified by being the .NET-native
  choice and by SDCA needing no additional native library — the failure that ruled out SpatiaLite in
  [ADR 017](017-spatial-querying.md) and would rule out a gradient-boosting trainer here.
- Every figure the model produces is attributable to an exact trainer, feature set, and dataset. None
  of them says anything about real-world severity assessment, and the report, the API payload, the
  dashboard, and the README all say so in their own words rather than deferring to this document.
- Changing the corpus changes the model version, which changes what every subsequent stored
  prediction is attributed to. That is the intended behaviour and the reason the version is composed
  rather than hand-written.
