/**
 * Value formatting and the severity vocabulary.
 *
 * Nothing here touches the DOM or the network: these are the small pure conversions the render
 * paths lean on, kept together so they can be exercised directly.
 */

/** Ordering for severity comparisons. Unknown sorts below everything rather than above it. */
export const SEVERITY_RANK = { Critical: 4, High: 3, Medium: 2, Low: 1, Unknown: 0 };

export const SEVERITY_COLOUR = {
  Critical: '#ef476f',
  High: '#ff9f1c',
  Medium: '#ffd166',
  Low: '#64dfdf',
  Unknown: '#aebdca',
};

export const colourFor = (severity) => SEVERITY_COLOUR[severity] ?? SEVERITY_COLOUR.Unknown;

/** Coerces a snapshot-supplied count to a number, so it can never carry markup into innerHTML. */
export const asCount = (value) => (Number.isFinite(Number(value)) ? Number(value) : 0);

export const plural = (count, word) => `${count} ${word}${count === 1 ? '' : 's'}`;

/** Turns an identifier such as ARMED_CONFLICT or navalBlockade into readable prose. */
export function humanise(value) {
  const spaced = String(value ?? '')
    .replace(/_/g, ' ')
    .replace(/([a-z])([A-Z])/g, '$1 $2')
    .toLowerCase()
    .trim();
  return spaced ? spaced[0].toUpperCase() + spaced.slice(1) : '—';
}
