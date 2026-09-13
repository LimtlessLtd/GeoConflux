# Turning on ACLED and UCDP

*Written 2026-09-13. Access terms and endpoints are the providers' to change; check theirs, not this.*

These two adapters are the difference between a dashboard that is deep in three theatres and one
that covers the world. They are built, tested against recorded fixtures, and shipped **dormant**.
Each needs one credential, and neither credential is in this repository or ever will be — see the
rule in [`CLAUDE.md`](../../CLAUDE.md) and [ADR 021](../adr/021-outbound-trust-boundary.md).

Nothing here changes what a clone does. Without secrets the adapters stay off, the build publishes
exactly as it did before, and the workflow log says which sources were unavailable rather than
leaving a reader to infer it from a thin map.

---

## 1. Request the credentials

**ACLED** — register for API access at <https://acleddata.com/>. The current API authenticates an
*account*, so what you end up with is the registered email address and a password, not an API key.
An older key-and-email scheme existed; a deployment still carrying that configuration stays dormant
rather than issuing requests the current API rejects.

**UCDP** — request an access token from the maintainers. The API answers a tokenless request with
HTTP 401 and a sentence naming the header it wants, which is how `x-ucdp-access-token` was
confirmed rather than guessed.

Both are free for research use. Read each provider's terms yourself; they govern what a deployment
may do with the data, and this document does not restate them.

## 2. Add them as repository secrets

`Settings → Secrets and variables → Actions → New repository secret`:

| Secret | Holds |
| --- | --- |
| `ACLED_USERNAME` | The registered ACLED account's email address |
| `ACLED_PASSWORD` | That account's password |
| `UCDP_ACCESS_TOKEN` | The UCDP access token |

`.github/workflows/pages.yml` reads them into provider configuration. Each adapter enables itself
only when its own secret is non-empty, so adding one does not switch on the other.

The ACLED email is treated as a credential *and* as a personal identifier, and is redacted from logs
on both counts. Adding a secret elsewhere means adding it to `ProviderSecretRedactor` too; a value
that is not listed there is a value that can reach a log.

## 3. Confirm it worked

Push, or run the workflow by hand, and read the run:

- The export step logs a line per provider. A configured adapter says it is live and how often it
  polls; an unconfigured one says it is not enabled and makes no request.
- The verify step prints how many distinct regions the snapshot placed observations in. Turning a
  credential on should move that number sharply, and the notice about neither dataset being
  configured should stop appearing.
- The published coverage panel breaks the same figures down per region, language, tier and platform.

If a credential is wrong, the run says so rather than quietly publishing less. A rejected ACLED
credential is reported as a rejected credential — answering a bad password with "zero events" is the
failure mode hardest to notice from a dashboard, and it is specifically tested against.

---

## What the Pages deployment does not do

**No backfill.** The export runs against a throwaway database that is discarded with the job, so the
checkpoint that makes a backfill resumable does not survive it. Configuring `BackfillSince` there
would re-read the same first few windows on every push and never get any further back. The published
snapshot is the live window, and that is all it claims to be.

Backfill belongs to a deployment that keeps its database — the API host, running continuously. There
it is configured per provider:

```jsonc
"Providers": {
  "Acled": {
    "BackfillSince": "2024-01-01T00:00:00Z",  // how far back to walk; absent means no backfill
    "BackfillWindow": "7.00:00:00",           // how much history per step
    "MaxRequestsPerPoll": 4,                  // the bound, shared with the live window
    "MaxItemsPerPoll": 500                    // rows per request, not per poll
  }
}
```

The walk moves backwards one window per step, writing down how far it has asked after each one, and
stops when the budget runs out. The live window is read first and may consume the whole budget: a
busy day now outranks a quiet week in 2019. A deployment whose live window regularly leaves nothing
for history needs a larger `MaxRequestsPerPoll`, and the logs say so rather than the walk silently
never advancing.

## Rate limits

UCDP documents an allowance of 5,000 requests a day. `MaxRequestsPerPoll` is the ceiling per poll,
so the worst case is that number times the polls in a day — at the shipped 24-hour interval and a
budget of 4, six requests a week. ACLED's guidance is that paginated calls do not count against its
rate limits. Neither is a reason to spend requests carelessly, which is why the bound exists at all
rather than being left to a retry policy.

## What this does not buy you

Coverage of these two datasets is coverage of what ACLED and UCDP code, which is a great deal and is
not everything. Both are compiled from reported incidents, so a conflict nobody reports is a conflict
neither contains, and that gap is not randomly distributed — it is largest exactly where reporting is
most dangerous. The coverage panel is there to keep that visible rather than let a filled-in map
imply it has been solved.

The other half of the problem is unaffected by any credential: an event this system cannot **place**
is an event it cannot draw, whatever coded it. See
[the global coverage assessment](../global-coverage-plan.md) for why the gazetteer, not the sources,
is the binding constraint.
