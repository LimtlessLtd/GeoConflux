# ADR 028: Three of the four FIRMS filters are built, so FIRMS stays off for these theatres

- Status: Accepted
- Date: 2026-09-13
- Builds on [ADR 015](015-live-provider-ingestion.md), which established the polling adapters, and on
  [ADR 027](027-no-troop-movement-mapping.md), whose habit of writing down what was declined this
  follows.

## Context

NASA FIRMS publishes VIIRS thermal anomalies with a free key, at roughly one to three hours of
latency for these theatres. The adapter has existed since Sprint 4 and has always shipped disabled.

The temptation is to point it at Ukraine, Yemen and Ethiopia and turn it on. The assessment is blunt
about why that would be a mistake: **fire is not war.** In Yemen, gas flaring burns continuously and
reads as a permanent detection. In Ethiopia, seasonal agricultural burning produces thousands of
detections a week across exactly the regions of interest. Enabling FIRMS bounded to these countries
without filtering would fill the map with farming and industry and label it conflict.

So the sprint plan names four filters as the precondition for enabling it: a persistent-flare mask, a
cropland mask, a fire-radiative-power threshold, and night-only selection. The adapter is not the
work; this is.

## Decision

### Three filters are built

- **Fire radiative power threshold.** Crop-residue burning is weak and the events this system looks
  for are not. A detection whose dataset reports no power at all is kept rather than read as zero —
  reading "not stated" as "too weak" would silently empty the feed for any deployment on a dataset
  that omits the column, and an empty feed is the failure mode hardest to notice.
- **Night-only selection.** The cheapest discriminator available, because agricultural burning is
  overwhelmingly a daytime activity. FIRMS publishes a `daynight` flag per detection. An absent flag
  reads as day, so a night-only filter rejects it rather than admitting an unknown on a technicality.
- **Persistent-source mask.** A location burning on several separate days is infrastructure, not an
  event. This is the flare mask, and it is **derived from the data rather than taken from a published
  list of flare sites**. That is a real trade: a published list would be authoritative and would work
  on the first poll, whereas deriving it means the mask sees only as far back as the request window,
  so a deployment that wants it has to ask for several days at a time. What deriving it buys is that
  it needs no external dataset, it stays correct when a new flare is lit, and it cannot go stale.
  Counting distinct *days* rather than distinct detections matters: a satellite passing twice in one
  night over one genuine fire would otherwise look exactly like a permanent source.

Every rejection is counted and logged by reason rather than summed into one "filtered" figure. A poll
that yields nothing has said something useful if it also says four hundred detections were dropped as
persistent sources; the same empty result with no breakdown is indistinguishable from a bad
credential.

### The cropland mask is not built, and cannot honestly be built here

A cropland mask needs a land-cover dataset. The realistic candidates are ESA WorldCover at 10 m and
MODIS MCD12Q1 at 500 m, and both are the wrong shape for this repository in the same way:

- **Committing one is not possible.** Global coverage runs to gigabytes. Even cropped to three
  theatre bounding boxes, these are raster products that would dwarf everything else in the
  repository, and the gazetteer extract at 895 KB is already the largest file here by an order of
  magnitude.
- **Fetching one at build time is excluded** by the same rule that governs the gazetteer: the build is
  hermetic and a clone must run offline with no credentials. [ADR 026](026-gazetteer-sourcing.md)
  settled that, and a raster is not a reason to reopen it.
- **Querying one at poll time** means a per-request dependency on a third-party raster service for
  every detection, which is a latency and availability problem attached to the highest-volume source
  in the system.
- **Approximating one is worse than not having it.** A hand-drawn "these squares are farmland"
  overlay would be a coordinate written from recollection, wearing a different hat. It is the same
  thing ADR 026 refuses.

A downsampled cropland-fraction grid over just the three theatres would be a few tens of kilobytes
and would be committable. The obstacle is not size; it is that there is no way from inside this
repository to obtain that grid from a source whose provenance can be stated and checked. Sourcing it
is a piece of work in its own right, and guessing at it is not an acceptable substitute.

### So FIRMS stays disabled for these theatres

The sprint plan says these filters, **and only then** enable it. Three of four is not four, so the
precondition is not met and the adapter stays off. It also still requires a credential this
repository does not hold, so nothing changes operationally today; what changes is that the reason is
recorded rather than left as an oversight.

## Consequences

The three filters that exist address most of what the cropland mask was for. Crop-residue burning is
both daytime and low-power, so night-only selection and a power floor already remove the bulk of it,
and the persistent-source mask handles flaring, which the cropland mask never would have. The
residual exposure is night-time high-power agricultural fires, which are uncommon — but "uncommon"
is not "filtered", and this paragraph is not a licence to enable it.

A deployment that holds the land-cover data this repository cannot is free to enable FIRMS having
made its own judgement. The options exist and the filters are configurable. What is recorded here is
that *this* repository does not make that judgement on anyone's behalf.

The honest summary is the one worth carrying forward: the adapter was never the work, the filtering
is, and three quarters of the filtering is done.
