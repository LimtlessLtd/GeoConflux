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
  const flaggedDemo = incidents.some((incident) => incident.isDemo)
    || observations.some((observation) => observation.isDemo);
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
  const live = observations.filter((observation) => !observation.isDemo).length;
  const demo = observations.length - live;

  if (live > 0 && demo > 0) {
    return {
      reveal: true,
      label: 'MIXED',
      text: `${plural(live, 'live report')} from public feeds, ${plural(demo, 'replayed demo record')} — each labelled individually`,
    };
  }

  if (live > 0) {
    return {
      reveal: true,
      label: 'LIVE',
      text: 'Real headlines from public news and humanitarian feeds. Categories and severities are this system’s assessments, not the publishers’',
    };
  }

  return { reveal: false, label: null, text: 'Synthetic replay data — not live reporting' };
}
