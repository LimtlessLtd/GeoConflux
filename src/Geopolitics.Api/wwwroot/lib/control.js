/**
 * What the page may say about who holds a place.
 *
 * This is the only panel in the dashboard that renders a *conclusion*. Everything else shows what a
 * source said or what a deterministic rule derived; this says what the system thinks. So the rules
 * here are stricter than anywhere else in lib, and three of them are about refusing:
 *
 *   - a place with too little evidence says so, and never shows a confident-looking actor name;
 *   - a stale assessment reads as "last asserted", never as "held";
 *   - a contested place shows both actors and prefers neither.
 *
 * And one rule is about geometry: nothing here produces a line, a polygon, or a fill between two
 * assessed places. The absence of a front line is a property of the payload, and this module must
 * not reintroduce one by rendering.
 */

import { asCount } from './format.js';

/** Ordered by how much a reader should trust the row, not alphabetically. */
const VERDICT_LABEL = {
  Assessed: 'assessed',
  Contested: 'contested',
  Stale: 'last asserted',
  Insufficient: 'not assessed',
};

/** Which rows carry an actual assertion. Used to decide what may be drawn at all. */
const ASSERTING = new Set(['Assessed', 'Contested']);

export const verdictLabel = (verdict) => VERDICT_LABEL[verdict] ?? 'not assessed';

/** False only when the payload is absent or malformed. An empty assessment is worth showing. */
export function hasControl(report) {
  return Array.isArray(report?.places);
}

/**
 * The assessed places, in the order the server put them, with counts coerced.
 *
 * Ordering is not re-decided here: the server ordered by how recent the evidence is, which is the
 * order a reader should read them in. Re-sorting by actor or by name would bury the newest.
 */
export function controlRows(report) {
  return (report?.places ?? []).map((place) => ({
    place: String(place?.place ?? ''),
    country: place?.countryCode ? String(place.countryCode) : '',
    actor: place?.actor ? String(place.actor) : '',
    verdict: String(place?.verdict ?? 'Insufficient'),
    label: verdictLabel(place?.verdict),
    ageDays: asCount(place?.ageDays),
    evidenceCount: (place?.evidence ?? []).length,
    sourceCount: asCount(place?.sourceCount),
    statement: String(place?.statement ?? ''),
    asserting: ASSERTING.has(String(place?.verdict ?? '')),
    demo: place?.evidenceIsDemo === true,
  }));
}

/**
 * The places that may be drawn on the globe at all.
 *
 * Only rows that actually assert something, and only rows with a coordinate. A place that could not
 * be resolved has nowhere to be drawn, and an unassessed one has nothing to say — drawing either
 * would put a marker on the map that means less than a reader would take it to mean.
 */
export function drawablePlaces(report) {
  return (report?.places ?? [])
    .filter((place) => ASSERTING.has(String(place?.verdict ?? '')))
    .map((place) => ({ ...place, demo: place?.evidenceIsDemo === true }))
    .filter((place) => Number.isFinite(place?.latitude) && Number.isFinite(place?.longitude))
    .map((place) => ({
      place: String(place.place ?? ''),
      actor: place.actor ? String(place.actor) : '',
      verdict: String(place.verdict),
      latitude: Number(place.latitude),
      longitude: Number(place.longitude),
      precision: String(place.precision ?? 'Country'),
      ageDays: asCount(place.ageDays),
      contested: String(place.verdict) === 'Contested',

      // Carried onto the marker as well as into the list. A globe shows no statement, so a
      // synthetic assessment drawn identically to a real one is exactly the misreading the
      // statement exists to prevent.
      demo: place.demo === true,
    }));
}

/**
 * One line summarising the whole assessment.
 *
 * The empty case is passed through from the server rather than rewritten, because the server knows
 * why it is empty — no dataset credential — and a client-side "nothing to show" would read as
 * nothing happening.
 */
export function controlSummary(report) {
  if (!hasControl(report)) return '';

  return String(report.note ?? '');
}

/** How the assessment was arrived at, carried from the payload so the page cannot overstate it. */
export const controlMethod = (report) => String(report?.method ?? '');

/**
 * The standing caveat that has to sit next to any rendering of this.
 *
 * Not taken from the payload, because it is about the *drawing* rather than about the data: a reader
 * looking at scattered markers will try to join them up, and this is the sentence that says the
 * joining would be theirs rather than the system's.
 */
export const NOT_A_FRONT_LINE =
  'Each marker is one assessed place. Nothing is drawn between them, because nothing was reported '
  + 'between them — this is not a front line, and joining them up would be your inference rather '
  + 'than this system’s.';
