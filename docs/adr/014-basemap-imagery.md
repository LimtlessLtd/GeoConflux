# ADR 014: Use Esri's keyless public map services for globe imagery

- Status: Accepted
- Date: 2026-09-11

## Context

The globe shipped with Natural Earth II, the world texture bundled with the CesiumJS distribution.
It needs no credentials, which is why it was chosen, but it carries no borders, no place names, and
no detail beyond continental scale. The camera had to be held ~2,200 km back on every fly-to because
anything closer resolved to a featureless wash.

For a dashboard whose entire subject is *where something happened*, that is a real limitation:
a marker over a blur communicates nothing about the place.

## Options

| Option | Detail | Credential | Verdict |
| --- | --- | --- | --- |
| Natural Earth II (bundled) | continental | none | Keep as fallback |
| Cesium ion world imagery + terrain | very high, plus 3D terrain | ion token | Rejected — needs a credential and carries a quota |
| Google Map Tiles API | very high | API key | Rejected — needs a key, and the terms do not permit this use |
| OpenStreetMap raster tiles | high, map style only | none | Rejected — the tile usage policy discourages application use |
| **Esri public map services** | **to zoom 23, sub-metre in populated areas** | **none** | **Chosen** |

## Decision

Use Esri's public ArcGIS map services as the default basemap, reached with
`ArcGisMapServerImageryProvider.fromUrl` against the classic REST endpoints, which require no API
key. Three styles are offered — satellite imagery, street map, topographic — and the satellite view
carries Esri's boundaries-and-places layer on top, because imagery alone shows terrain but not
jurisdiction, and jurisdiction is the point here.

Natural Earth II stays, as the fallback rather than as a decorative extra.

Two consequences of that fallback are deliberate:

- Imagery is attached *after* the viewer is constructed, not passed to its constructor, so a tile
  host that is slow or unreachable costs detail rather than costing the globe. The remote provider
  is bounded by a 12-second timeout and the layer swap happens only once the new imagery is in hand.
- When the fallback engages the page says so, rather than silently showing a worse map.

Esri's terms require attribution. Cesium surfaces the service's own `copyrightText` in its credit
container automatically; the CSS styles that container for legibility over imagery and deliberately
does not hide it.

## Consequences

- The globe now shows coastlines, borders, cities, and — when zoomed — individual buildings. The
  fly-to distance dropped from 2,200 km to 400 km because there is finally something to look at.
- 400 km is still a regional framing rather than street level, and that is intentional. Most
  incidents are placed at a gazetteer centroid for a named region, so a closer default camera would
  render a precision the data does not have. A visitor who wants more can zoom.
- The page gains a third-party runtime dependency. It already depended on the CesiumJS CDN, so this
  is a difference of degree, and unlike the CDN this one degrades instead of failing.
- These services are free for this kind of use and attributed accordingly, but they are not licensed
  for arbitrary commercial redistribution. Anyone forking this for commercial use needs to revisit
  the basemap, which is part of why the provider list is a single registry at the top of `app.js`.
- Real 3D terrain and 3D buildings remain out of reach without a Cesium ion token. That is a
  credential, so it stays out.
