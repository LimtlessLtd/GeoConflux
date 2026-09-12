/**
 * How a classification and its provenance are rendered.
 *
 * These build markup as strings, so every interpolated value that came from a feed is escaped here.
 * The numbers are not escaped because they are put through Number() first and can only be numeric
 * by the time they reach the template.
 */

import { escapeHtml } from './html.js';

/**
 * Turns a classification method into something a reader can judge.
 *
 * The distinction that matters on screen is not which vendor answered but whether a judgement was
 * inferred at all, so the three cases are named rather than the providers.
 */
export function describeMethod(method) {
  const value = String(method ?? '');
  if (value.startsWith('source-declared+')) {
    return { kind: 'mixed', label: 'source + model', detail: value };
  }
  if (value === 'source-declared') {
    return { kind: 'declared', label: 'stated by source', detail: value };
  }
  if (value.startsWith('ai:')) {
    return { kind: 'model', label: value.slice(3), detail: value };
  }
  if (value === 'keyword') {
    return { kind: 'heuristic', label: 'keyword match', detail: value };
  }
  return { kind: 'unknown', label: value || 'unrecorded', detail: value };
}

/**
 * Renders confidence as a number and a bar next to the method that produced it.
 *
 * A category shown on its own reads as a fact. Showing "HIGH" beside "41% · keyword match" is the
 * whole point of carrying provenance through the pipeline, so this is used everywhere a category
 * is displayed rather than only in the drawer.
 */
export function confidenceChip(confidence, method) {
  const value = Number(confidence);
  if (!Number.isFinite(value) || value <= 0) {
    const unscored = describeMethod(method);
    return `<span class="conf conf-none" title="No confidence was recorded for this classification.">
        unscored · ${escapeHtml(unscored.label)}</span>`;
  }

  const percent = Math.round(value * 100);
  const described = describeMethod(method);
  const title = `${percent}% confidence, produced by ${described.detail}. `
    + 'This is a stated confidence in a classification, not a probability that the event occurred.';

  return `<span class="conf conf-${escapeHtml(described.kind)}" title="${escapeHtml(title)}">
      <span class="conf-meter" aria-hidden="true"><i style="width:${percent}%"></i></span>
      <span class="conf-value">${percent}%</span>
      <span class="conf-method">${escapeHtml(described.label)}</span>
    </span>`;
}

/**
 * The trained model's second opinion on one observation.
 *
 * Rendered as a distinct row rather than mixed in with the classification chips, and only ever
 * described as an opinion. The pipeline's severity is the one that was acted on; this one has no
 * standing over it, and a reader must not have to work that out from the layout.
 *
 * Agreement is shown as well as disagreement. A panel that only appeared when the two differed
 * would make disagreement look like an error state rather than the ordinary outcome it is.
 */
export function modelOpinion(opinion, appliedSeverity) {
  if (!opinion) return '';

  const agrees = !opinion.disagreesWithApplied;
  const percent = Math.round((opinion.confidence ?? 0) * 100);

  return `<div class="model-opinion${agrees ? '' : ' is-divergent'}"
      title="A conventional model trained on a small synthetic corpus. It is recorded for comparison and never sets the severity an incident is stored with. Model: ${escapeHtml(opinion.modelVersion)}">
      <span class="model-tag">ML</span>
      <span>${agrees
    ? `agrees: <strong class="sev sev-${escapeHtml(opinion.severity)}">${escapeHtml(opinion.severity)}</strong>`
    : `would have said <strong class="sev sev-${escapeHtml(opinion.severity)}">${escapeHtml(opinion.severity)}</strong>,
           not <strong class="sev sev-${escapeHtml(appliedSeverity ?? 'Unknown')}">${escapeHtml(appliedSeverity ?? 'Unknown')}</strong>`}
      </span>
      <span class="model-confidence">${percent}%</span>
    </div>`;
}
