import { test } from 'node:test';
import assert from 'node:assert/strict';

import { selectVisibleIncidents } from '../../src/Geopolitics.Api/wwwroot/lib/incidents.js';

const incident = (id, overrides = {}) => ({
  id,
  severity: 'Medium',
  eventType: 'ARMED_CONFLICT',
  location: { latitude: 0, longitude: 0 },
  occurredAt: '2026-09-10T00:00:00Z',
  ...overrides,
});

const ids = (result) => result.map((each) => each.id);

test('incidents are ordered newest first', () => {
  const result = selectVisibleIncidents([
    incident('older', { occurredAt: '2026-09-01T00:00:00Z' }),
    incident('newest', { occurredAt: '2026-09-12T00:00:00Z' }),
    incident('middle', { occurredAt: '2026-09-06T00:00:00Z' }),
  ]);

  assert.deepEqual(ids(result), ['newest', 'middle', 'older']);
});

test('the severity filter admits the threshold itself and everything above it', () => {
  const all = [
    incident('critical', { severity: 'Critical' }),
    incident('high', { severity: 'High' }),
    incident('medium', { severity: 'Medium' }),
    incident('low', { severity: 'Low' }),
  ];

  assert.deepEqual(ids(selectVisibleIncidents(all, { severity: 'High' })).sort(), ['critical', 'high']);
  assert.equal(selectVisibleIncidents(all, { severity: 'Low' }).length, 4);
});

test('an incident of unrecognised severity is filtered out by any real threshold', () => {
  const all = [incident('odd', { severity: 'Catastrophic' }), incident('high', { severity: 'High' })];

  assert.deepEqual(ids(selectVisibleIncidents(all, { severity: 'Low' })), ['high']);
});

test('no filters means everything is shown', () => {
  const all = [incident('a'), incident('b', { severity: 'Unknown' })];

  assert.equal(selectVisibleIncidents(all).length, 2);
  assert.equal(selectVisibleIncidents(all, {}).length, 2);
});

test('the type filter matches exactly, and an empty type means every type', () => {
  const all = [
    incident('conflict', { eventType: 'ARMED_CONFLICT' }),
    incident('maritime', { eventType: 'MARITIME_INCIDENT' }),
  ];

  assert.deepEqual(ids(selectVisibleIncidents(all, { type: 'MARITIME_INCIDENT' })), ['maritime']);
  assert.equal(selectVisibleIncidents(all, { type: '' }).length, 2);
});

test('the located-only filter removes incidents that were never placed', () => {
  const all = [incident('placed'), incident('unplaced', { location: null })];

  assert.deepEqual(ids(selectVisibleIncidents(all, { locatedOnly: true })), ['placed']);
  assert.equal(selectVisibleIncidents(all, { locatedOnly: false }).length, 2);
});

test('during replay only incidents already revealed are shown', () => {
  const all = [incident('shown'), incident('not-yet')];
  const replay = { active: true, revealed: new Set(['shown']) };

  assert.deepEqual(ids(selectVisibleIncidents(all, {}, replay)), ['shown']);
});

test('when replay is not running every incident is eligible again', () => {
  const all = [incident('a'), incident('b')];

  assert.equal(selectVisibleIncidents(all, {}, { active: false, revealed: new Set() }).length, 2);
});

test('filters combine rather than override one another', () => {
  const all = [
    incident('keep', { severity: 'Critical', eventType: 'MARITIME_INCIDENT' }),
    incident('wrong-type', { severity: 'Critical', eventType: 'ARMED_CONFLICT' }),
    incident('too-low', { severity: 'Low', eventType: 'MARITIME_INCIDENT' }),
    incident('unplaced', { severity: 'Critical', eventType: 'MARITIME_INCIDENT', location: null }),
  ];

  const result = selectVisibleIncidents(all, {
    severity: 'High', type: 'MARITIME_INCIDENT', locatedOnly: true,
  });

  assert.deepEqual(ids(result), ['keep']);
});

test('the collection the caller holds is not reordered underneath it', () => {
  const all = [
    incident('older', { occurredAt: '2026-09-01T00:00:00Z' }),
    incident('newest', { occurredAt: '2026-09-12T00:00:00Z' }),
  ];

  selectVisibleIncidents(all);

  assert.deepEqual(ids(all), ['older', 'newest']);
});

test('an iterator is accepted, because the caller holds a Map', () => {
  const map = new Map([['a', incident('a')], ['b', incident('b')]]);

  assert.equal(selectVisibleIncidents(map.values()).length, 2);
});
