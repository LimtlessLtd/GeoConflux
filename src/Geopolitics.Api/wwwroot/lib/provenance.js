/**
 * What the page is allowed to claim about the data it is showing.
 *
 * This is the labelling guarantee in one place: demo and replay records must never be presented as
 * live reporting. Both decisions below are pure functions of the loaded records, so the rule can be
 * asserted directly rather than inferred from whether a banner happened to be visible in a browser.
 */

import { plural } from './format.js';

/**
 * Whether the demo notice can be hidden.
 *
 * Fail safe. The notice is hidden ONLY on positive evidence that nothing on screen is demo data:
 * records were actually loaded, none of them are flagged, and the snapshot (if any) does not
 * declare itself demo. An empty database must not be mistaken for a live deployment, which is
 * exactly what a plain "any record flagged?" test would do on a fresh start.
 */
export function canHideDemoNotice(incidents, observations, mode, meta) {
  const records = incidents.length + observations.length;
  const flaggedDemo = incidents.some((incident) => provenanceOf(incident) === 'recorded')
    || observations.some((observation) => provenanceOf(observation) === 'recorded');
  const snapshotDeclaresDemo = mode === 'static' && (meta?.isDemoData ?? true) !== false;

  return records > 0 && !flaggedDemo && !snapshotDeclaresDemo;
}

/**
 * How the provenance banner should describe a set of observations.
 *
 * `reveal` is false for the demo-only case on purpose: that path sets the wording but leaves the
 * banner's visibility to the caller, which has already decided it from the fuller test above.
 */
export function provenanceSummary(observations) {
  const counts = countByProvenance(observations);
  const live = counts.polled + counts.collected;

  if (live === 0) {
    return { reveal: false, label: null, text: 'Synthetic replay data — not live reporting' };
  }

  const parts = [];

  if (counts.polled > 0) {
    parts.push(`${plural(counts.polled, 'report')} polled from public feeds`);
  }

  if (counts.collected > 0) {
    parts.push(`${plural(counts.collected, 'report')} gathered by an OSINT collection run`);
  }

  if (counts.recorded > 0) {
    parts.push(`${plural(counts.recorded, 'replayed demo record')}`);
  }

  // MIXED whenever demo records share the page with real ones, because that is the case where a
  // single label would be wrong about part of what it describes.
  const label = counts.recorded > 0 ? 'MIXED' : (counts.polled > 0 ? 'LIVE' : 'COLLECTED');

  return {
    reveal: true,
    label,
    text: `${parts.join(', ')} — each labelled individually. Categories and severities are this system’s assessments, not the sources’`,
  };
}

/**
 * Splits a set of observations by where they came from.
 *
 * Reads `provenance` and falls back to `isDemo` for a payload written before that field existed.
 * The fallback resolves to Polled rather than Collected because that is what every non-demo record
 * was at the time such a payload could have been produced.
 */
export function countByProvenance(observations) {
  const counts = { recorded: 0, polled: 0, collected: 0 };

  for (const observation of observations) {
    counts[provenanceOf(observation)] += 1;
  }

  return counts;
}

/** One record's provenance, as a lowercase key, tolerating an older payload. */
export function provenanceOf(observation) {
  switch (observation?.provenance) {
    case 'Collected': return 'collected';
    case 'Polled': return 'polled';
    case 'Recorded': return 'recorded';
    default: return observation?.isDemo ? 'recorded' : 'polled';
  }
}

/**
 * The chip a record carries in the feed: DEMO for synthetic, COLLECTED for a bundle, nothing for a
 * polled feed item.
 *
 * Polled gets no chip because it is the unremarkable case — a page of chips reads as noise and
 * stops being read, which would cost the DEMO label the attention it exists to command.
 */
export function provenanceChip(observation) {
  switch (provenanceOf(observation)) {
    case 'recorded':
      return { className: 'demo-chip', text: 'DEMO', title: 'Synthetic replay data, not live reporting' };
    case 'collected':
      return {
        className: 'collected-chip',
        // The date is the label, not decoration. A collected record's freshness is when it was
        // gathered, and without that on the row a fortnight-old bundle reads exactly like a feed
        // item from this morning.
        text: collectedLabel(observation.collectedAt),
        title: observation.collectedAt
          ? `Gathered by an OSINT collection run on ${observation.collectedAt.slice(0, 10)}`
          : 'Gathered by an OSINT collection run',
      };
    default:
      return null;
  }
}

/** `COLLECTED 2026-09-12`, or bare `COLLECTED` when the run date is missing. */
export function collectedLabel(collectedAt) {
  if (typeof collectedAt !== 'string' || collectedAt.length < 10) {
    return 'COLLECTED';
  }

  return `COLLECTED ${collectedAt.slice(0, 10)}`;
}
