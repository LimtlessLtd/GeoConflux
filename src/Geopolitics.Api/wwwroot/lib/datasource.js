/**
 * Choosing where the dashboard reads from.
 *
 * The order is the whole contract: a live backend is preferred, the exported snapshot is the
 * fallback, and a source that throws is treated exactly like one that reported itself unavailable.
 * A probe that rejects rather than returning false is the normal case on GitHub Pages, where there
 * is no backend and the request fails at the network layer.
 */

/**
 * Returns the first source whose probe succeeds, or null when none does.
 *
 * Probes run in sequence rather than in parallel so that a reachable backend is never passed over
 * because the snapshot answered first.
 */
export async function resolveDataSource(sources) {
  for (const source of sources) {
    try {
      if (await source.probe()) return source;
    } catch {
      // A network or CORS failure here just means this source is not available; try the next.
    }
  }

  return null;
}
