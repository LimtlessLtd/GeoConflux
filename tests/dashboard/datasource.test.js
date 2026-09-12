import { test } from 'node:test';
import assert from 'node:assert/strict';

import { resolveDataSource } from '../../src/Geopolitics.Api/wwwroot/lib/datasource.js';

/**
 * The fallback from a live backend to the exported snapshot is what makes the published page work
 * at all: GitHub Pages serves files only, so the API probe there fails at the network layer rather
 * than returning a polite false.
 */

const source = (name, probe) => ({ name, probe });
const reachable = (name) => source(name, async () => true);
const unreachable = (name) => source(name, async () => false);
const refuses = (name) => source(name, async () => { throw new Error('ECONNREFUSED'); });

test('a reachable backend is preferred over the snapshot', async () => {
  const chosen = await resolveDataSource([reachable('api'), reachable('static')]);

  assert.equal(chosen.name, 'api');
});

test('an unavailable backend falls back to the snapshot', async () => {
  const chosen = await resolveDataSource([unreachable('api'), reachable('static')]);

  assert.equal(chosen.name, 'static');
});

test('a probe that throws is treated exactly like one that reported itself unavailable', async () => {
  // This is the ordinary case on GitHub Pages, not an edge case.
  const chosen = await resolveDataSource([refuses('api'), reachable('static')]);

  assert.equal(chosen.name, 'static');
});

test('no reachable source resolves to null rather than to a broken source', async () => {
  assert.equal(await resolveDataSource([refuses('api'), unreachable('static')]), null);
  assert.equal(await resolveDataSource([]), null);
});

test('a source is not probed once an earlier one has answered', async () => {
  // Probes run in sequence so a reachable backend is never passed over because the snapshot
  // answered first, and so the snapshot is not fetched when it will not be used.
  const probed = [];
  const record = (name, answer) => source(name, async () => { probed.push(name); return answer; });

  await resolveDataSource([record('api', true), record('static', true)]);

  assert.deepEqual(probed, ['api']);
});

test('every source is probed, in order, until one answers', async () => {
  const probed = [];
  const record = (name, answer) => source(name, async () => { probed.push(name); return answer; });

  const chosen = await resolveDataSource([
    record('api', false), record('mirror', false), record('static', true),
  ]);

  assert.deepEqual(probed, ['api', 'mirror', 'static']);
  assert.equal(chosen.name, 'static');
});
