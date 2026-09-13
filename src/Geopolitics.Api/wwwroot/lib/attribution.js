/**
 * The difference between *reported by* and *claimed on*, in the one place the page decides it.
 *
 * A wire item comes from an organisation with an editorial process and a reputation it is unwilling
 * to spend. A Telegram post comes from a handle. Both arrive as text and both render as a row, and
 * a reader must never have to guess which they are looking at — so the wording is a pure function of
 * the record and can be asserted rather than hunted for in a browser.
 *
 * The label is about the source, never about the claim. Nothing here says a post is false or that a
 * wire is true; it says who is standing behind each, which is the only thing the record establishes.
 */

import { asCount, plural } from './format.js';

/**
 * Whether this record is a user-generated claim.
 *
 * Reads `tier` and falls back to the observation kind for a payload written before that field
 * existed. The fallback resolves to published, because at the time such a payload could have been
 * produced nothing was gated — labelling old wire items as claims would be the more misleading of
 * the two errors, and it would be loud rather than quiet.
 */
export function isClaim(observation) {
  return observation?.tier === 'UserGenerated';
}

/** A claim nothing independent supports yet. */
export function isHeldClaim(observation) {
  return isClaim(observation) && observation?.status === 'Uncorroborated';
}

/**
 * Who this came from, and in which of the two senses.
 *
 * `channel` is the platform and handle together, because a handle is only unique within its
 * platform: telegram/reuters is not mastodon/reuters, and a label that dropped the platform would
 * let one be read as the other.
 */
export function attributionOf(observation) {
  if (!isClaim(observation)) {
    return { kind: 'reported', by: observation?.sourceName ?? 'an unnamed source' };
  }

  const platform = observation?.platform;
  const channel = observation?.channel;

  if (!platform || !channel) {
    // The open write path: user-generated with nothing behind it. Named as what it is rather than
    // given a channel it does not have.
    return { kind: 'claimed', by: null };
  }

  return { kind: 'claimed', by: `${platform}/${channel}`, platform, channel };
}

/** One line naming the source in the right grammar for what it is. */
export function attributionLine(observation) {
  const attribution = attributionOf(observation);

  if (attribution.kind === 'reported') {
    return `Reported by ${attribution.by}`;
  }

  return attribution.by
    ? `Claimed on ${attribution.by}`
    : 'Submitted anonymously through the open endpoint';
}

/**
 * The chip a claim carries in the feed, or null for published reporting.
 *
 * Published reporting gets no chip, for the same reason a polled feed item gets no provenance chip:
 * a page of chips reads as noise and stops being read, which would cost the labels that matter the
 * attention they exist to command.
 *
 * The held case and the corroborated case are different chips, not one chip with a different colour.
 * "Uncorroborated" is a statement about evidence and it stops being true the moment a second source
 * arrives, so a reader who learns to skip the chip would be skipping the change.
 */
export function claimChip(observation) {
  if (!isClaim(observation)) {
    return null;
  }

  const attribution = attributionOf(observation);

  if (isHeldClaim(observation)) {
    return {
      className: 'claim-chip claim-uncorroborated',
      text: 'UNCORROBORATED CLAIM',
      title: attribution.by
        ? `Posted on ${attribution.by}. No independent source supports it yet, so it has not formed an incident.`
        : 'Submitted anonymously. No independent source supports it, so it has not formed an incident.',
    };
  }

  return {
    className: 'claim-chip claim-corroborated',
    text: 'CLAIM · CORROBORATED',
    title: attribution.by
      ? `Posted on ${attribution.by}, and supported by at least one independent source.`
      : 'An anonymous submission, attached to an incident other sources established.',
  };
}

/**
 * What the page may say about a held claim, in a sentence.
 *
 * Says what is and is not known rather than casting doubt. The system has not judged the post false;
 * it has declined to assert the event on one source, which is a different and smaller statement.
 */
export const heldClaimNote = () =>
  'Shown because it was posted, not because it is confirmed. A single post cannot open an incident '
  + 'here; this one joins the map the moment an independent source reports the same thing.';

/**
 * How the feed summarises the mix of published reporting and claims on screen.
 *
 * Returns null when there are no claims at all, so the ordinary case adds no furniture. The counts
 * are the point: "four of forty" and "forty of forty" are the same page with completely different
 * standing, and only a number distinguishes them.
 */
export function claimSummary(observations) {
  const records = Array.isArray(observations) ? observations : [];
  const claims = records.filter(isClaim);

  if (claims.length === 0) {
    return null;
  }

  const held = claims.filter(isHeldClaim).length;
  const heldPart = held > 0
    ? `, ${held} of which ${held === 1 ? 'is' : 'are'} uncorroborated and ${held === 1 ? 'has' : 'have'} formed no incident`
    : ', all corroborated by an independent source';

  return `${plural(claims.length, 'record')} of ${records.length} ${claims.length === 1 ? 'is' : 'are'} `
    + `a claim posted on an open platform rather than published reporting${heldPart}.`;
}

/**
 * The platforms represented on screen, busiest first.
 *
 * Useful where the coverage panel is not on screen: a feed that is entirely one platform is the
 * bias this project measures, and seeing it in the feed itself costs nothing.
 */
export function platformsPresent(observations) {
  const counts = new Map();

  for (const observation of Array.isArray(observations) ? observations : []) {
    const { platform } = attributionOf(observation);

    if (platform) {
      counts.set(platform, asCount(counts.get(platform)) + 1);
    }
  }

  return [...counts.entries()]
    .map(([platform, count]) => ({ platform, count }))
    .sort((a, b) => b.count - a.count || a.platform.localeCompare(b.platform));
}
