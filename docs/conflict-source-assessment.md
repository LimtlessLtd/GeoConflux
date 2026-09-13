# Getting timely localised data for Ukraine, Yemen and Tigray

Date: 2026-09-12. Scope: what open sources can actually place conflict activity on a map for these
three theatres, at what latency and what spatial precision, and what this repository would have to
change to use them.

This was written as an assessment rather than an implementation, and it is kept as written — it is
the record of what was surveyed and how each claim was checked. Sprint 9 is now acting on it, so
individual findings carry a note where the code has moved on. Plan section 43 tracks what is built.

The brief asked for "accurate timely localised info … an accurate map of warzones and battles/troop
movements". Those are three different questions with three different answers, and conflating them is
the main way projects like this go wrong:

1. **Where is the front line?** Polygons of territorial control. Obtainable daily for Ukraine, not at
   all for the other two.
2. **What happened, where?** Points with coordinates and a date. Obtainable for all three, at one to
   fourteen days of lag depending on the theatre.
3. **Where are the units?** Order of battle and movement. **Not obtainable from open sources at any
   useful fidelity or timeliness.** See [Troop movements](#troop-movements-the-part-that-cannot-be-done).

Answering (1) and (2) well is a real product. Claiming (3) is how a project stops being trustworthy.

## Two findings that block everything else

### The ACLED adapter points at a host that no longer exists

> **Resolved in Sprint 9.** The adapter was migrated to the OAuth API described below. The finding is
> kept as written, because it is the record of what was wrong and of how it was verified; what
> follows is the diagnosis, not a description of the code today. Plan section 20 has the migration.

`src/Geopolitics.Infrastructure/Sources/Providers/AcledEventSource.cs` builds requests against
`acled/read?key=…&email=…`, and `appsettings.json` sets `BaseAddress` to `https://api.acleddata.com/`.
That hostname does not resolve:

```text
acleddata.com                    200
api.acleddata.com                curl: (6) Could not resolve host: api.acleddata.com
ucdpapi.pcr.uu.se                301
firms.modaps.eosdis.nasa.gov     200
deepstatemap.live                200
```

Every other candidate host answers, so this is not local DNS. ACLED moved to a new platform and
retired the old API; the previous system accepted existing keys until 15 September 2025 and issued no
new ones after launch. The current API is at `https://acleddata.com/api/` and authenticates with
OAuth against `https://acleddata.com/oauth/token` — access tokens valid 24 hours, refresh tokens 14
days — or with a session cookie. `https://acleddata.com/api/acled/read?limit=1` returns 403 rather
than a connection failure, which is the expected answer for an unauthenticated request to a live
endpoint.

The adapter ships disabled, so nothing is failing today. But the single most valuable source for all
three of these conflicts is currently unreachable code, and the failure would only appear on the
first live poll after someone supplied a credential. The key/email query-parameter shape is also gone
— the options record needs a token flow, not a second string field.

ACLED matters more than the rest combined for this brief: it is the only source that covers Ukraine,
Yemen *and* Ethiopia in one schema, with coordinates, a coded event type and a fatality count. It is
also the one source this repository already knows how to treat as authoritative about position.

### The gazetteer is the ceiling on everything that is not ACLED or FIRMS

`ObservationKindRules.MayDeclareCoordinates` permits `Satellite` and `ExternalEvent` to state their
own coordinates, and nothing else. That is the correct rule and ADR 025 defends it well. Its
consequence is that **every textual source — RSS, collected bundles, manual submission — is placed
only as precisely as `Gazetteer.cs` can place it.**

`Gazetteer.cs` holds 205 entries: 148 whole countries, 16 seas and straits, and **41
settlement-precision places for the entire world**. Sub-country coverage for the three theatres in
question:

| Theatre | What resolves today | What fighting is reported in |
|---|---|---|
| Ukraine | Kyiv, Kharkiv, Odesa, Donetsk, Kharkiv Oblast, Donetsk Oblast | Pokrovsk, Kupiansk, Kostiantynivka, Siversk, Huliaipole, Vovchansk, and ~150 other settlements |
| Yemen | Sana'a, Aden | Marib, Hodeidah, Taiz, Saada, Al-Jawf, Shabwah, Ad-Dali, and ~330 districts |
| Tigray | *nothing* — `ET` resolves as a country centroid | Mekelle, Adigrat, Shire, Axum, Zalambesa, Tselemti, Mai Degusha |

A country-precision entry places an event at the national centroid. For Ethiopia that is roughly 600
km from Mekelle. The `PlacePrecision.Country` flag means the globe will at least not draw it as
though it were exact, which is the enum working as designed — but "correctly labelled as useless" is
still useless.

So the bottleneck is not source discovery. It is that a Tigray report can be ingested, deduplicated,
enriched, scored and correlated today, and still have nowhere to go on the map. Expanding the
gazetteer for these three theatres unlocks every text source at once, and no amount of new adapters
substitutes for it.

Name variants make this harder than counting rows suggests. ADR 025 already found this for Arabic —
`اليمن` and `السودان` had to resolve before an Arabic bundle placed anything. Tigray needs Ge'ez
script for both Tigrinya and Amharic (መቐለ), plus Latin transliterations that are genuinely unstable
(Mekelle / Mekele / Mek'ele / Makale). Ukraine needs Ukrainian and the Russian exonyms that appear in
Russian-language reporting of the same place.

## What can be obtained, by question

### Territorial control

**Ukraine — DeepStateMap.** `https://deepstatemap.live/api/history/last` answers an unauthenticated
GET with a 627 KB GeoJSON `FeatureCollection`: polygons with bilingual status names
(`Окуповано /// Occupied`, `Статус невідомий /// Unknown status`) and styling. A community mirror at
`github.com/cyterat/deepstate-map-data` republishes a daily multipolygon snapshot at 03:00 UTC, which
is the more honest thing to consume because it is versioned and its update time is stated.

Two caveats. The properties carry **no timestamp**, so a consumer must stamp the fetch time itself
and cannot tell from the payload alone how fresh the underlying assessment is. And DeepState is a
volunteer organisation with no published data licence — republishing derived polygons on a public
dashboard is a licensing question to settle before building, not after.

**Ukraine — ISW / Critical Threats.** Daily control-of-terrain assessments with an interactive
ArcGIS-backed map. There is no documented public data download, and the maps carry attribution terms.
The right relationship here is to cite and link, not to scrape.

**Yemen.** No machine-readable control product exists. Control is comparatively static — Houthi
authority across the north-west, the internationally recognised government and the STC across the
south and east, with Marib, Taiz and the Hodeidah approaches contested — and periodic control maps
from analysis outfits are published as images or PDFs, not as data.

**Tigray.** Nothing. No public control-polygon product covers the 2026 clashes. Who holds what is
contested, reported anecdotally, and moves faster than anyone is publishing.

### Coded events with coordinates

**ACLED** is the primary answer for all three. Human-coded, carries
latitude and longitude, event type and fatality count, which is why the existing adapter correctly
declares all of them on the envelope rather than re-deriving weaker versions. Free registration gives
tiered access; anything commercial needs a licence conversation. Release cadence is weekly, so expect
several days of lag — fine for a map of what has happened, not a live feed.

**UCDP Georeferenced Event Dataset** is the strongest free complement. It was unused when this was
written; the adapter was built in Sprint 9. The API at `https://ucdpapi.pcr.uu.se/api/<resource>/<version>` is free of charge, needs a
token requested from the maintainer and sent as `x-ucdp-access-token`, and allows 5,000 requests a
day. The yearly datasets are at v26.1; **GED Candidate** publishes monthly with under a month's lag
and is at v26.0.7. Events are geocoded to individual villages where the sourcing supports it, and —
importantly for this project — the records carry `where_prec`, an explicit statement of how precisely
the coordinate is known. That maps almost directly onto `PlacePrecision`, so UCDP can populate the
map without the honesty problem that usually comes with borrowed coordinates.

**Yemen Data Project** publishes a CSV of every Saudi-led coalition air raid 2015–2022, plus separate
sets for US–UK strikes (Jan 2024 – Jan 2025) and Israeli strikes. It is the definitive record of the
air war and it **has no coordinates** — deliberately, because the open-source collection cannot
support them. Locations are governorate → district → area. It is therefore only usable here after a
Yemen district gazetteer exists, and it would resolve at district precision, not settlement.

**GDELT** updates every 15 minutes and is free, but it is machine-coded from news text with coarse
and frequently wrong geocoding. It is a tip-off and volume signal, not a mapping source. Ingesting it
as though it were coded event data would swamp the deterministic classifier with noise and put dots
in the wrong places at high confidence.

### Near-real-time signals

**NASA FIRMS** (adapter exists, disabled) gives VIIRS 375 m thermal anomalies. A free MAP_KEY allows
5,000 transactions per 10 minutes against `/api/area/csv/[KEY]/[SOURCE]/[BBOX]/[DAYS]/[DATE]`.
Latency tiers are URT under 60 seconds, RT under 60 minutes, NRT thereafter — **but URT covers the US
and Canada only**, so for these theatres the practical floor is RT/NRT, roughly one to three hours.

The serious caveat is that fire is not war. In Yemen, gas flaring burns continuously and reads as a
permanent detection. In Ethiopia, seasonal agricultural burning produces thousands of detections a
week across exactly the regions of interest. Enabling FIRMS bounded to these countries without a
persistent-flare mask, a cropland mask and a fire-radiative-power threshold would fill the map with
agriculture and label it conflict. That filtering is the work; the adapter is not.

**Ukraine air-raid alerts.** The official API at `api.ukrainealarm.com` returns 403 unauthenticated
and issues keys on request; `alerts.in.ua` offers a comparable service built on the same official
feed plus regional administration channels. Oblast-level, sub-minute, with SSE for live updates. This
is the most timely signal available for any of the three conflicts — and the least specific. An alert
means *a warning was issued for this oblast*, not *something was struck here*. It is excellent for
tempo and worthless for placement.

**UNOSAT damage assessments**, distributed through the Humanitarian Data Exchange, are the only
public building-level georeferenced damage data for Ukraine — manually vectorised by analysts from
satellite imagery. Episodic rather than a feed, and authoritative when it exists.

### Troop movements: the part that cannot be done

There is no open source that gives unit positions and movement at useful fidelity and timeliness.
What exists, and why each falls short:

- **Commercial imagery** (Planet, Maxar, Umbra). Tasking latency of hours to days, per-scene cost,
  and licences that forbid the republication this project would need.
- **Sentinel-1 SAR.** Free and genuinely powerful for damage mapping — there is now published,
  peer-reviewed open tooling for Ukraine-scale destruction mapping from Sentinel-1 time series — but
  6–12 day revisit and a processing pipeline well outside this repository's scope.
- **Geolocated social footage.** Minutes old and the fastest thing in existence, but individually
  unverifiable and an actively poisoned channel. ADR 025 already anticipated this: Tier B open social
  is specified and deliberately unbuilt, because the corroboration gate that stops one uncorroborated
  post from forming an incident does not exist yet. That ordering is correct and should hold.
- **Analyst unit markers** on DeepState and ISW. Daily, inferred, and not published as data.

The recommendation is to **not map troop movements**, and to say so on the page. Map control change
and event density, and let a reader draw the inference. Asserting unit positions from open sources
would be precisely the unsupported claim the rest of this project is built to refuse — the same
failure as an LLM setting a coordinate, arriving through a door marked "analysis" instead.

## Per-conflict summary

| | Ukraine | Yemen | Tigray |
|---|---|---|---|
| Control polygons | DeepState, daily, free, licence unclear | none machine-readable | none |
| Coded events | ACLED + UCDP GED | ACLED + UCDP; YDP has no coordinates | ACLED only, and thinner than reality |
| Fastest signal | alerts API, sub-minute, oblast-level | FIRMS, flare-contaminated | FIRMS, burn-contaminated |
| Gazetteer work needed | ~150 settlements, UK/RU variants | ~120 districts, Arabic variants | ~80 woredas, Ge'ez + unstable Latin |
| Honest ceiling | settlement, daily | district, weekly | zone, weekly, with gaps |

**Tigray deserves singling out.** It is the hardest of the three and the one where a map will most
overstate its own completeness. The conflict resumed in 2026: clashes at Mai Degusha in Tselemti
district on 29 January, federal drone strikes on vehicles in Central Tigray the following day, a TDF
split with the breakaway Tigray Peace Force, and by August a TPLF–Eritrea alignment with Eritrean
forces at Zalambesa — a full reversal of the 2020–22 alignment. Meanwhile the ACLED Ethiopia Peace
Observatory ended its fortnightly updates on 1 July 2025, folding Ethiopia into monthly regional
overviews. Dedicated coverage thinned roughly six months before the shooting restarted.

Add recurring communications blackouts and thin independent verification, and any map of Tigray will
be sparser than the war. That is a first-class finding to publish, not a footnote: a quiet district
means nobody reported, not that nothing happened.

## Recommended order of work

1. ~~**Migrate the ACLED adapter to the current API.**~~ Done in Sprint 9. OAuth token flow,
   `https://acleddata.com/api/`, token cached across polls, refresh with a password-grant fallback.
2. ~~**Add a UCDP GED Candidate adapter** as `ObservationKind.ExternalEvent`.~~ Done in Sprint 9.
   `where_prec` is carried through `ObservationEnvelope.DeclaredPrecision`, and the same seam carries
   ACLED's `geo_precision`.
3. ~~**Expand the gazetteer for the three theatres, with script variants.**~~ Done in Sprint 9, from
   a committed Wikidata (CC0) extract rather than by hand. ADR 026 records the sourcing, the licence
   comparison, and the collision rules.
4. **Model territorial control as polygons.** The domain currently has point observations and
   incidents; a control layer is a new concept, not a new adapter, and needs an ADR. DeepState's
   licence question should be settled first.
5. **Enable FIRMS only behind conflict filtering.** Partly done in Sprint 9: the FRP threshold,
   night-only selection and a persistent-source mask are built, the cropland mask is not, and FIRMS
   stays disabled because three of four is not four. See ADR 028.
6. ~~**Do not build troop-movement mapping.**~~ Declined and recorded in Sprint 9, as ADR 027.

Items 1 and 2 need no new domain concepts and no gazetteer work, because both sources are permitted
to state their own coordinates. They are the shortest path from here to real conflict data on the
globe. Item 3 is what makes everything else worth having.

## Examined and set aside

- **ReliefWeb API v2.** Free and well-suited to humanitarian reporting on all three countries, and
  ReliefWeb is already polled here as RSS. A direct probe of `api.reliefweb.int` returned 403, which
  is most likely user-agent filtering rather than the API being unavailable — recorded as unverified
  rather than as a finding, and worth re-testing from the application.
- **X and Weibo.** Unchanged from ADR 025: gated, and not to be worked around.
- **Commercial feeds** (Janes, Dataminr and similar). Real capability, priced for institutions, and
  incompatible with a repository that must run from a clone with no credentials.
