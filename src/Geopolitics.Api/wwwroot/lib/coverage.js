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

/**
 * The four breadth tables: region, language, tier, platform.
 *
 * Each carries a note saying what it does and does not establish, because every one of them is
 * readable as a claim about the world when it is only a claim about this system's reach. A table of
 * countries with Ukraine at the top says where this system reads, not where fighting is.
 *
 * Empty sections are dropped rather than shown as zero. A platform table with nothing in it before
 * any open social has been collected is furniture; the source list below says why it is empty, which
 * is the part a reader actually needs.
 */
export function breadthSections(report) {
  return [
    {
      key: 'region',
      title: 'By country',
      note: 'Where placed records were placed. A country low here is one this system reads little '
        + 'about, which is not the same as one where little happened.',
      footnote: notLookedAtNote(report),
      rows: report?.byRegion ?? [],
    },
    {
      key: 'language',
      title: 'By language',
      note: 'The language of the original text, as the source stated it or enrichment identified it. '
        + 'Reading widely in one language is not global reach, and this is the figure that says which.',
      rows: report?.byLanguage ?? [],
    },
    {
      key: 'tier',
      title: 'By source tier',
      note: 'Published reporting against claims posted on open platforms. A claim is shown and placed '
        + 'like anything else; what it cannot do is open an incident on its own.',
      rows: report?.byTier ?? [],
    },
    {
      key: 'platform',
      title: 'By platform',
      note: 'Which open platforms the claims came from. One platform standing in for the world is the '
        + 'bias that looks most like working coverage.',
      rows: report?.byPlatform ?? [],
    },
  ].filter((section) => section.rows.some((row) => asCount(row?.count) > 0));
}

/**
 * The sentence that separates *not looked at* from *nothing found*.
 *
 * The table above lists only countries where something was placed, so an absent country is invisible
 * — and an invisible country reads as a quiet one. These are different statements and the difference
 * is not decoration: one is a limit of this system and the other would be a claim about the world.
 *
 * The theatres are named because they are the exception. They are watched deliberately and are shown
 * even when they are empty, which is what makes their emptiness mean "nothing reached us" rather
 * than "nobody was looking".
 */
export function notLookedAtNote(report) {
  const watched = (report?.theatres ?? []).map((theatre) => theatre?.theatre).filter(Boolean);
  const listed = (report?.byRegion ?? []).filter((row) => asCount(row?.count) > 0).length;

  const watchedPart = watched.length > 0
    ? ` The ${plural(watched.length, 'theatre')} above — ${watched.join(', ')} — are watched `
      + 'deliberately and are listed even when empty, so an empty one means nothing reached this '
      + 'system rather than that nobody was looking.'
    : '';

  return `${listed} ${listed === 1 ? 'country appears' : 'countries appear'} here. A country absent `
    + 'from this list was not looked at — no source this deployment runs is aimed at it — which is a '
    + `different statement from one where nothing was found.${watchedPart}`;
}

/** Plain wording for each outcome a source can have, keyed by what the collection tool wrote. */
const OUTCOME_WORDING = {
  collected: 'read, and contributed',
  'nothing matched': 'read in full; nothing matched this brief',
  'no public posts': 'exists, and publishes nothing readable',
  capped: 'had matching posts, all of which the diversity caps dropped',
  unreachable: 'could not be read',
};

/**
 * What each source the last run asked actually gave.
 *
 * This is the half of coverage that counting cannot supply. A channel that refused, a channel that
 * publishes nothing, a channel read that had nothing relevant to say, and a channel the caps emptied
 * are four different statements that reduce to the same absence — and an absence on a map reads as
 * "nothing happened there" rather than as "we did not see".
 *
 * An outcome this build has never heard of is shown rather than dropped. The string comes from a
 * tool that is versioned separately, and a coverage panel that silently omitted what it could not
 * name would be the exact failure it exists to prevent.
 */
export function sourceOutcomes(report) {
  return (report?.sources ?? []).map((source) => {
    const matched = asCount(source?.matched);
    const collected = asCount(source?.collected);
    const read = asCount(source?.read);
    const wording = OUTCOME_WORDING[source?.outcome] ?? `reported as “${source?.outcome ?? 'unstated'}”`;

    const detail = source?.reason
      ? source.reason
      : (read > 0 ? `${read} posts read, ${matched} matched, ${collected} kept` : null);

    return {
      channel: source?.channel ?? 'an unnamed source',
      outcome: source?.outcome ?? 'unstated',
      empty: collected === 0,
      text: wording,
      detail,
    };
  });
}

/**
 * One sentence on how much of what was asked actually answered.
 *
 * Returns null when nothing has been collected at all, which is a different fact from every source
 * having failed and must not be rendered as one. A deployment that has run no collection and a
 * deployment whose every channel refused look identical on the map; here they do not.
 */
export function sourceSummary(report) {
  const sources = sourceOutcomes(report);

  if (sources.length === 0) {
    return null;
  }

  const contributing = sources.filter((source) => !source.empty).length;

  return `${sources.length} source${sources.length === 1 ? '' : 's'} were asked in the last run and `
    + `${contributing} contributed. The rest are listed with what came of asking, because a source `
    + 'that gave nothing and a source that was never asked are different things.';
}
