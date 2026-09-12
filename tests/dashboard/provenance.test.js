import { test } from 'node:test';
import assert from 'node:assert/strict';

import {
  canHideDemoNotice, provenanceSummary,
} from '../../src/Geopolitics.Api/wwwroot/lib/provenance.js';

/**
 * Demo and replay records must never be presented as live reporting. That is a project rule rather
 * than a display preference, so the decision is asserted here directly instead of being inferred
 * from whether a banner happened to be visible in a browser.
 */

const live = (id) => ({ id, isDemo: false });
const demo = (id) => ({ id, isDemo: true });

test('an empty database is not mistaken for a live deployment', () => {
  // The fail-safe case. A plain "is any record flagged?" test would answer "none are" here and
  // hide the notice on a fresh start, which is exactly the wrong answer.
  assert.equal(canHideDemoNotice([], [], 'live', null), false);
});

test('the notice stays up when any record is flagged as demo', () => {
  assert.equal(canHideDemoNotice([demo('i1')], [live('o1')], 'live', null), false);
  assert.equal(canHideDemoNotice([live('i1')], [demo('o1')], 'live', null), false);
});

test('the notice comes down only on positive evidence that nothing shown is demo data', () => {
  assert.equal(canHideDemoNotice([live('i1')], [live('o1')], 'live', null), true);
});

test('a snapshot that declares itself demo keeps the notice up regardless of the record flags', () => {
  assert.equal(canHideDemoNotice([live('i1')], [live('o1')], 'static', { isDemoData: true }), false);
});

test('a snapshot that says nothing about itself is treated as demo', () => {
  // Absent metadata must not read as a live claim.
  assert.equal(canHideDemoNotice([live('i1')], [live('o1')], 'static', {}), false);
  assert.equal(canHideDemoNotice([live('i1')], [live('o1')], 'static', null), false);
  assert.equal(canHideDemoNotice([live('i1')], [live('o1')], 'static', undefined), false);
});

test('a snapshot that explicitly declares itself live can hide the notice', () => {
  assert.equal(canHideDemoNotice([live('i1')], [live('o1')], 'static', { isDemoData: false }), true);
});

test('only an explicit false counts as a live declaration', () => {
  // A truthy-ish or absent value is not a claim, and must not be read as one.
  for (const value of [true, 'false', 0, null, undefined]) {
    assert.equal(
      canHideDemoNotice([live('i1')], [], 'static', { isDemoData: value }), false,
      `isDemoData: ${JSON.stringify(value)} should not be read as a live declaration`,
    );
  }
});

test('a mixed feed says so, and labels both kinds', () => {
  const summary = provenanceSummary([live('a'), live('b'), demo('c')]);

  assert.equal(summary.reveal, true);
  assert.equal(summary.label, 'MIXED');
  assert.match(summary.text, /2 live reports/);
  assert.match(summary.text, /1 replayed demo record/);
  assert.match(summary.text, /each labelled individually/);
});

test('a fully live feed does not claim the assessments came from the publishers', () => {
  const summary = provenanceSummary([live('a')]);

  assert.equal(summary.reveal, true);
  assert.equal(summary.label, 'LIVE');
  assert.match(summary.text, /Real headlines/);
  assert.match(summary.text, /not the publishers/);
});

test('a single live report reads in the singular', () => {
  assert.match(provenanceSummary([live('a'), demo('b')]).text, /1 live report from/);
});

test('a replay-only feed is named as synthetic and never revealed as live', () => {
  const summary = provenanceSummary([demo('a'), demo('b')]);

  assert.equal(summary.reveal, false);
  assert.equal(summary.label, null);
  assert.match(summary.text, /Synthetic replay data/);
  assert.match(summary.text, /not live reporting/);
});

test('no observations at all is described as synthetic rather than as live', () => {
  assert.equal(provenanceSummary([]).reveal, false);
  assert.match(provenanceSummary([]).text, /Synthetic replay data/);
});
