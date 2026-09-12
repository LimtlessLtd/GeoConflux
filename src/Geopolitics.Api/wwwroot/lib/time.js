/**
 * Time display, and the decision about which clock a record is aged against.
 *
 * The run basis is module state rather than a parameter threaded through every call site, because
 * it is set once when the page works out what it is showing and then never changes. The `now`
 * argument on the functions below defaults to the real clock and exists so the basis rule can be
 * asserted against fixed instants instead of against whenever the suite happens to run.
 */

import { plural } from './format.js';

/**
 * What relative times are measured against.
 *
 * In live mode that is the visitor's clock, because the events really did just arrive. In static
 * mode it must NOT be: the snapshot describes a recorded run, and measuring it against "now" would
 * stamp synthetic events on real straits and cities as though they happened this afternoon. The
 * basis becomes the moment the run was recorded, and the suffix says so.
 */
let runBasis = null;
let runSuffix = 'ago';

/**
 * Records the instant a static snapshot was produced.
 *
 * An unparseable timestamp leaves the basis alone rather than setting it to NaN. A snapshot with a
 * corrupt date then reads against the visitor's clock, which is wrong but bounded; setting it to
 * NaN would render every relative time as "NaN days ago".
 */
export function setRunBasis(generatedAt, suffix = 'before the run') {
  const recordedAt = Date.parse(generatedAt ?? '');
  if (!Number.isFinite(recordedAt)) return false;

  runBasis = recordedAt;
  runSuffix = suffix;
  return true;
}

/** Returns to the visitor's clock. Live mode never sets a basis; tests use this to isolate. */
export function clearRunBasis() {
  runBasis = null;
  runSuffix = 'ago';
}

/**
 * Which clock a given record's age should be measured against.
 *
 * A live report carries a real publication time from a real publisher, so the honest basis is the
 * visitor's own clock: it went out six hours ago, and saying "6hr ago" is a fact that stays true
 * however long after the run the page is read. A demo record carries an invented timestamp that
 * only means anything relative to the recorded run, and measuring that against now would stamp a
 * fabricated event on a real strait as though it had happened this afternoon.
 *
 * So the basis is chosen per record rather than per page. That is what lets the published snapshot
 * carry both kinds at once — which it does, and labels individually — without either one
 * misrepresenting the other.
 */
export const relativeBasis = (measureAgainstRun, now = Date.now()) => (
  measureAgainstRun && runBasis !== null
    ? { at: runBasis, suffix: runSuffix }
    : { at: now, suffix: 'ago' });

export const formatRelative = (value, measureAgainstRun = false, now = Date.now()) => {
  if (!value) return '';

  const { at, suffix } = relativeBasis(measureAgainstRun, now);
  const minutes = Math.round((at - new Date(value).getTime()) / 60000);

  // A source clock running slightly fast puts a publication time marginally in the future. The
  // pipeline already clamps that on the way in; this is the display-side equivalent, so a report
  // can never read as being from the future.
  if (minutes < 1) return suffix === 'ago' ? 'just now' : 'at the start of the run';
  if (minutes < 60) return `${minutes}m ${suffix}`;

  const hours = Math.round(minutes / 60);
  if (hours < 24) return `${hours}hr ${suffix}`;

  const days = Math.round(hours / 24);
  return `${plural(days, 'day')} ${suffix}`;
};

export const formatTime = (value) => (value
  ? new Intl.DateTimeFormat(undefined, { dateStyle: 'medium', timeStyle: 'short' }).format(new Date(value))
  : 'Unknown');

export const formatDate = (value) => (value
  ? new Intl.DateTimeFormat(undefined, { dateStyle: 'long' }).format(new Date(value))
  : 'an unknown date');

/**
 * The exact timestamp, phrased for a tooltip on a relative one.
 *
 * The relative form is what a reader wants at a glance; the exact form is what they want when the
 * glance raises a question, and a hover is cheaper than a click for that.
 */
export const exactTimeHint = (value) => (value
  ? `Source timestamp: ${formatTime(value)}`
  : 'This source stated no time of its own.');

/**
 * Whether a source actually gave a time for what it reported.
 *
 * When a source states none, normalisation falls back to the moment the report arrived, leaving
 * the two timestamps equal. Printing both would then present this system's own arrival time as
 * though the source had reported it, which is precisely the kind of borrowed authority the rest of
 * this dashboard is careful to avoid.
 */
export const statesOwnTime = (observation) => Boolean(observation.occurredAt)
  && Math.abs(new Date(observation.occurredAt).getTime() - new Date(observation.receivedAt).getTime()) > 60_000;
