import { test } from 'node:test';
import assert from 'node:assert/strict';

import {
  canHideDemoNotice, countByProvenance, provenanceChip, provenanceOf, provenanceSummary,
} from '../../src/Geopolitics.Api/wwwroot/lib/provenance.js';

/**
 * Demo and replay records must never be presented as live reporting. That is a project rule rather
 * than a display preference, so the decision is asserted here directly instead of being inferred
 * from whether a banner happened to be visible in a browser.
 */

const live = (id) => ({ id, isDemo: false, provenance: 'Polled' });
const demo = (id) => ({ id, isDemo: true, provenance: 'Recorded' });
const collected = (id) => ({ id, isDemo: false, provenance: 'Collected' });

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

test('a mixed feed says so, and labels every kind present', () => {
  const summary = provenanceSummary([live('a'), live('b'), collected('c'), demo('d')]);

  assert.equal(summary.reveal, true);
  assert.equal(summary.label, 'MIXED');
  assert.match(summary.text, /2 reports polled from public feeds/);
  assert.match(summary.text, /1 report gathered by an OSINT collection run/);
  assert.match(summary.text, /1 replayed demo record/);
  assert.match(summary.text, /each labelled individually/);
});

test('a fully live feed does not claim the assessments came from the sources', () => {
  const summary = provenanceSummary([live('a')]);

  assert.equal(summary.reveal, true);
  assert.equal(summary.label, 'LIVE');
  assert.match(summary.text, /polled from public feeds/);
  assert.match(summary.text, /not the sources/);
});

test('a collected-only feed is real but is not called live', () => {
  // A bundle is real reporting gathered at a stated moment. Calling it LIVE would overstate its
  // freshness, and calling it demo data would deny it is real at all.
  const summary = provenanceSummary([collected('a'), collected('b')]);

  assert.equal(summary.reveal, true);
  assert.equal(summary.label, 'COLLECTED');
  assert.match(summary.text, /2 reports gathered by an OSINT collection run/);
});

test('a single report reads in the singular', () => {
  assert.match(provenanceSummary([live('a'), demo('b')]).text, /1 report polled/);
  assert.match(provenanceSummary([collected('a'), demo('b')]).text, /1 report gathered/);
});

test('a payload written before provenance existed still reads correctly', () => {
  // The fallback resolves a non-demo record to Polled, which is what every such record was at the
  // time a payload without the field could have been produced.
  assert.equal(provenanceOf({ isDemo: true }), 'recorded');
  assert.equal(provenanceOf({ isDemo: false }), 'polled');
  assert.equal(provenanceOf({}), 'polled');
  assert.equal(provenanceOf(undefined), 'polled');
});

test('records are counted into the three classes', () => {
  const counts = countByProvenance([live('a'), collected('b'), collected('c'), demo('d')]);

  assert.deepEqual(counts, { recorded: 1, polled: 1, collected: 2 });
});

test('only synthetic and collected records carry a chip', () => {
  // Polled is the unremarkable case. Chipping everything would cost the DEMO label the attention it
  // exists to command.
  assert.equal(provenanceChip(live('a')), null);
  assert.deepEqual(provenanceChip(demo('b')), { className: 'demo-chip', text: 'DEMO' });
  assert.deepEqual(provenanceChip(collected('c')), { className: 'collected-chip', text: 'COLLECTED' });
});

test('the demo notice keys on provenance, not only the legacy flag', () => {
  // A record marked Recorded must hold the notice up even if isDemo were somehow absent.
  assert.equal(canHideDemoNotice([{ id: 'i', provenance: 'Recorded' }], [], 'live', null), false);
  assert.equal(canHideDemoNotice([{ id: 'i', provenance: 'Collected' }], [], 'live', null), true);
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
