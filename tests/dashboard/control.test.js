import { test } from 'node:test';
import assert from 'node:assert/strict';

import {
  NOT_A_FRONT_LINE,
  controlMethod,
  controlRows,
  controlSummary,
  drawablePlaces,
  hasControl,
  verdictLabel,
} from '../../src/Geopolitics.Api/wwwroot/lib/control.js';

/**
 * This is the only panel that renders a conclusion rather than a record, so its refusals are the
 * feature. A stale assessment that reads as "held", or an unassessed place that shows an actor
 * name, would each be the system asserting something its evidence does not support.
 */

const place = (overrides = {}) => ({
  place: 'Invented Town',
  countryCode: 'UA',
  latitude: 49,
  longitude: 36,
  precision: 'Settlement',
  actor: 'Invented State Forces',
  verdict: 'Assessed',
  asOf: '2026-09-12T00:00:00Z',
  ageDays: 2,
  evidence: [{ observationId: 'a' }, { observationId: 'b' }],
  sourceCount: 2,
  statement: 'Invented State Forces is assessed to hold this.',
  ...overrides,
});

const report = (places = [place()]) => ({
  places,
  placesWithEvidence: places.length,
  placesAssessed: places.filter((p) => p.verdict === 'Assessed' || p.verdict === 'Contested').length,
  method: 'Assessed per place from reports that say who holds it, never from reports of fighting.',
  note: 'Two places had control evidence and one reached an assertion.',
});

test('a missing or malformed payload leaves the panel empty rather than throwing', () => {
  assert.equal(hasControl(null), false);
  assert.equal(hasControl({}), false);
  assert.equal(hasControl({ places: [] }), true);
  assert.deepEqual(controlRows(null), []);
  assert.deepEqual(drawablePlaces(null), []);
  assert.equal(controlSummary(null), '');
  assert.equal(controlMethod(null), '');
});

test('each verdict reads as what it is, and none of them reads as certainty', () => {
  assert.equal(verdictLabel('Assessed'), 'assessed');
  assert.equal(verdictLabel('Contested'), 'contested');
  assert.equal(verdictLabel('Stale'), 'last asserted');
  assert.equal(verdictLabel('Insufficient'), 'not assessed');

  // An unrecognised verdict from a newer server must not default to the confident one.
  assert.equal(verdictLabel('SomethingNew'), 'not assessed');
  assert.equal(verdictLabel(undefined), 'not assessed');
});

test('rows keep the order the server chose', () => {
  // Ordered by how recent the evidence is. Re-sorting by name in the client would bury the newest,
  // which is the only one describing now.
  const rows = controlRows(report([
    place({ place: 'Newer', ageDays: 1 }),
    place({ place: 'Older', ageDays: 40, verdict: 'Stale' }),
  ]));

  assert.deepEqual(rows.map((row) => row.place), ['Newer', 'Older']);
  assert.equal(rows[1].label, 'last asserted');
});

test('a stale assessment is never labelled as held', () => {
  const [row] = controlRows(report([place({ verdict: 'Stale', ageDays: 45 })]));

  assert.equal(row.label, 'last asserted');
  assert.equal(row.asserting, false);
  assert.equal(row.ageDays, 45);
});

test('an unassessed place is not drawn and carries no confident actor claim', () => {
  const [row] = controlRows(report([place({ verdict: 'Insufficient', actor: null })]));

  assert.equal(row.actor, '');
  assert.equal(row.asserting, false);
  assert.deepEqual(drawablePlaces(report([place({ verdict: 'Insufficient', actor: null })])), []);
});

test('a contested place is drawn and flagged as contested rather than resolved to one side', () => {
  const drawable = drawablePlaces(report([place({ verdict: 'Contested', actor: null })]));

  assert.equal(drawable.length, 1);
  assert.equal(drawable[0].contested, true);
  assert.equal(drawable[0].actor, '');
});

test('a place with no coordinate is not drawn', () => {
  // Nowhere to put it. A marker at a default coordinate would be a claim about a place the resolver
  // could not find.
  assert.deepEqual(drawablePlaces(report([place({ latitude: null, longitude: null })])), []);
  assert.deepEqual(drawablePlaces(report([place({ latitude: 'x', longitude: 36 })])), []);
});

test('a stale assessment is not drawn on the map', () => {
  // It may be read in the list, where its age is beside it. On a globe a marker carries no age, and
  // a month-old assertion drawn identically to a two-day-old one is the misreading this avoids.
  assert.deepEqual(drawablePlaces(report([place({ verdict: 'Stale', ageDays: 45 })])), []);
});

test('the method and the note are passed through rather than rewritten', () => {
  // The server knows why an assessment is empty — no dataset credential — and a client-side
  // "nothing to show" would read as nothing happening.
  assert.match(controlMethod(report()), /never from reports of fighting/);
  assert.match(controlSummary(report()), /reached an assertion/);
});

test('an assessment resting on synthetic evidence is flagged in the list and on the marker', () => {
  // A globe shows no statement, so a synthetic assessment drawn identically to a real one is the
  // misreading the statement exists to prevent.
  const synthetic = report([place({ evidenceIsDemo: true })]);

  assert.equal(controlRows(synthetic)[0].demo, true);
  assert.equal(drawablePlaces(synthetic)[0].demo, true);

  assert.equal(controlRows(report())[0].demo, false);
  assert.equal(drawablePlaces(report())[0].demo, false);
});

test('the not-a-front-line caveat says whose inference joining the markers would be', () => {
  // A reader looking at scattered markers will try to join them up. This is the sentence that says
  // the joining is theirs.
  assert.match(NOT_A_FRONT_LINE, /not a front line/);
  assert.match(NOT_A_FRONT_LINE, /your inference/);
});
