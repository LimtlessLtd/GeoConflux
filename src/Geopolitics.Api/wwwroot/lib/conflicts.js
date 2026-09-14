/**
 * What the page may say about each conflict it saw reports about.
 *
 * Two readings have to be prevented and neither is obvious from the numbers alone. A conflict low in
 * the list looks quiet when it may only be uncovered, and a column of counts invites a reader to add
 * it up when one report can belong to two wars. Both are handled by wording rather than by omission,
 * which is why the wording is here and tested rather than assembled in a render path.
 *
 * Pure throughout: no DOM, no network. See docs/adr/024-dashboard-test-runner.md.
 */

import { asCount, plural } from './format.js';

/**
 * How a tempo verdict is shown. The key is that four of the six are not directions — a page that
 * rendered every conflict as an arrow would have to invent one for the cases where the honest answer
 * is that the comparison cannot be made.
 */
const VERDICT = {
  Rising: { label: 'More reported', tone: 'rising', arrow: '▲' },
  Falling: { label: 'Less reported', tone: 'falling', arrow: '▼' },
  Steady: { label: 'Little changed', tone: 'steady', arrow: '—' },
  NotStated: { label: 'Coverage changed', tone: 'unstated', arrow: '?' },
  TooThin: { label: 'Too thin to say', tone: 'thin', arrow: '·' },
  NoBaseline: { label: 'No baseline', tone: 'thin', arrow: '·' },
};

export const verdictOf = (verdict) =>
  VERDICT[verdict] ?? { label: 'Not stated', tone: 'unstated', arrow: '?' };

/**
 * Whether this row is asserting a direction at all.
 *
 * Used to keep the arrow off the rows that are not claims about the world. "Coverage changed" with a
 * downward arrow beside it is exactly the sentence-and-picture mismatch this panel exists to avoid.
 */
export const statesDirection = (verdict) => verdict === 'Rising' || verdict === 'Falling';

/** The volume line: what arrived, and from how many sources, always together. */
export function volumeLine(counts) {
  const reports = asCount(counts?.observations);
  const sources = asCount(counts?.sources);

  if (reports === 0) {
    return 'No reports this window.';
  }

  return `${plural(reports, 'report')} from ${plural(sources, 'source')}.`;
}

/**
 * The comparison against this conflict's own previous window.
 *
 * Against its own, never against another conflict's. "Ukraine 72, Tigray 9" reads as "Tigray is eight
 * times quieter" when it may mean nobody is reporting Tigray, and a page that ranks conflicts by
 * volume has made that comparison whether or not it meant to.
 */
export function baselineLine(tempo) {
  const previous = asCount(tempo?.previous?.observations);

  if (previous === 0) {
    return 'Nothing in the previous window to compare against.';
  }

  return `Previous window: ${plural(previous, 'report')} from ${plural(
    asCount(tempo?.previous?.sources),
    'source',
  )}.`;
}

/**
 * The headline for the whole panel: how much of the register this system saw anything about.
 *
 * The denominator is the point. "Forty conflicts" means nothing; "forty of the 319 a coding project
 * recorded" is a measurement, and it is a measurement of this system rather than of the world.
 */
export function coverageLine(report) {
  const seen = asCount(report?.seen);
  const registered = asCount(report?.registered);

  if (registered === 0) {
    return 'No conflict register is loaded, so nothing here can be put in proportion.';
  }

  const share = Math.round((seen / registered) * 100);

  return `Reports reached this system about ${seen} of the ${registered} conflicts in the register `
    + `(${share}%). The rest produced nothing here, which is a statement about this system's reach.`;
}

/**
 * What happened to the reports that belong to no conflict, and the distinction inside that number.
 *
 * "Nothing covers this" and "several things might and nothing said which" look identical in a count
 * of unassigned reports and mean opposite things: one is a hole in the register, the other is a
 * report that did not identify itself.
 */
export function unassignedLine(report) {
  const unassigned = asCount(report?.unassigned);
  const undecided = asCount(report?.undecided);

  if (unassigned === 0 && undecided === 0) {
    return '';
  }

  const parts = [];

  if (unassigned > 0) {
    parts.push(`${plural(unassigned, 'report')} reached no conflict in the register at all`);
  }

  if (undecided > 0) {
    parts.push(
      `${plural(undecided, 'report')} fell inside the area of one or more conflicts without `
      + 'identifying which',
    );
  }

  return `${parts.join(', and ')}.`;
}

/**
 * Orders the table. Busiest first, because that is what a reader scans for — and the note beside it
 * has to say that this orders by what was reported rather than by what happened.
 */
export function orderConflicts(conflicts) {
  return [...(conflicts ?? [])].sort(
    (a, b) => asCount(b?.current?.observations) - asCount(a?.current?.observations),
  );
}
