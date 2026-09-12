/**
 * Which incidents the filters leave on screen, and in what order.
 *
 * Taken as plain arguments rather than read from the controls, so the filtering and the ordering
 * can be exercised without a DOM.
 */

import { SEVERITY_RANK } from './format.js';

/**
 * Applies the filter controls and orders the result newest first.
 *
 * An unrecognised severity ranks 0 on both sides of the comparison: an incident carrying one is
 * filtered out by any threshold above Unknown, and a threshold naming one admits everything.
 */
export function selectVisibleIncidents(incidents, filters = {}, replay = { active: false }) {
  const minSeverity = SEVERITY_RANK[filters.severity] ?? 0;
  const { type, locatedOnly } = filters;

  return [...incidents]
    .filter((incident) => !replay.active || replay.revealed.has(incident.id))
    .filter((incident) => (SEVERITY_RANK[incident.severity] ?? 0) >= minSeverity)
    .filter((incident) => !type || incident.eventType === type)
    .filter((incident) => !locatedOnly || Boolean(incident.location))
    .sort((left, right) => new Date(right.occurredAt) - new Date(left.occurredAt));
}
