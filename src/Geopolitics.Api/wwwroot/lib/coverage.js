/**
 * What the page may say about how much of each theatre it has actually placed.
 *
 * A map is silent about its own gaps. Three dots over Tigray look identical whether three things
 * happened or three things were reported, and the difference is the whole of what this project tries
 * to be careful about. These functions turn the coverage payload into statements a reader can act
 * on, and they are pure so the wording can be asserted directly rather than hunted for in a browser.
 */

import { asCount, plural } from './format.js';

/** Coarsest last, so a reader scans from "we know where" down to "we know roughly where". */
const PRECISION_ORDER = ['Exact', 'Settlement', 'Region', 'Country'];

const PRECISION_LABEL = {
  Exact: 'exactly located',
  Settlement: 'to a town',
  Region: 'to an area',
  Country: 'to a country only',
};

/**
 * Orders a precision breakdown from most to least precise.
 *
 * Sorting by count instead would put the coarsest bucket first whenever it is the largest, which is
 * precisely when a reader most needs to see that the precise buckets are nearly empty.
 */
export function orderByPrecision(byPrecision) {
  return [...(byPrecision ?? [])]
    .filter((entry) => asCount(entry?.count) > 0)
    .sort((a, b) => {
      const left = PRECISION_ORDER.indexOf(a?.category);
      const right = PRECISION_ORDER.indexOf(b?.category);
      return (left < 0 ? PRECISION_ORDER.length : left) - (right < 0 ? PRECISION_ORDER.length : right);
    });
}

export const precisionLabel = (category) => PRECISION_LABEL[category] ?? 'placed by an unstated method';

/**
 * One sentence saying what this theatre has and where it came from.
 *
 * The empty case is the important one, and it deliberately does not read as reassurance. "Nothing
 * placed" invites the reading that nothing happened, so the wording says what is actually known:
 * nothing reached the map, which is a statement about this system rather than about the war.
 */
export function coverageSummary(theatre) {
  const placed = asCount(theatre?.placedCount);
  const sources = (theatre?.bySource ?? []).filter((entry) => asCount(entry?.count) > 0);

  if (placed === 0) {
    return 'Nothing from this theatre reached the map in this run. That is a statement about what '
      + 'was reported and collected, not about what happened.';
  }

  const named = sources.map((entry) => entry.category).join(', ');
  const from = named ? ` from ${named}` : '';

  return `${plural(placed, 'observation')} placed${from}.`;
}

/**
 * How much of this theatre the lexicon could place at all.
 *
 * Reported beside the observation counts because it is the ceiling on every text source at once, and
 * because it is most of why one theatre looks emptier than another. Without it a reader compares
 * 2,105 Ukrainian place names against 163 Tigrayan ones without ever being told that is the
 * comparison they are making.
 */
export function lexiconNote(theatre) {
  const places = asCount(theatre?.gazetteerPlaces);

  return places === 0
    ? 'No place names are held for this theatre, so no text report naming one can be drawn.'
    : `${places.toLocaleString('en-GB')} place names held for this theatre.`;
}

/**
 * Whether the coverage panel has anything worth showing.
 *
 * False only when the payload is absent or malformed. A report whose every theatre is empty is very
 * much worth showing — that is the case the panel exists for.
 */
export function hasCoverage(report) {
  return Array.isArray(report?.theatres) && report.theatres.length > 0;
}

/**
 * The gaps that belong to the report as a whole rather than to any one theatre.
 *
 * Both numbers are limits a reader is entitled to before trusting the counts above: observations
 * that named somewhere unresolvable, and names the lexicon refuses to resolve because they denote
 * more than one place.
 */
export function gapNotes(report) {
  const notes = [];
  const unplaced = asCount(report?.unplacedCount);
  const ambiguous = asCount(report?.ambiguousNameCount);

  if (unplaced > 0) {
    notes.push(`${plural(unplaced, 'observation')} named a place that could not be resolved. `
      + `${report?.unplacedNote ?? ''}`.trim());
  }

  if (ambiguous > 0) {
    notes.push(`${ambiguous.toLocaleString('en-GB')} place names are held but not used, because each `
      + 'denotes more than one place and resolving one would put a marker in the wrong place.');
  }

  return notes;
}
