import { beforeEach, test } from 'node:test';
import assert from 'node:assert/strict';

import {
  clearRunBasis, exactTimeHint, formatDate, formatRelative, formatTime, relativeBasis,
  setRunBasis, statesOwnTime,
} from '../../src/Geopolitics.Api/wwwroot/lib/time.js';

const READING_NOW = Date.parse('2026-09-12T12:00:00Z');
const RUN_RECORDED_AT = '2026-09-01T00:00:00Z';
const ago = (iso) => iso;

// The basis is module state, so every test starts from live mode rather than from whatever the
// previous one left behind.
beforeEach(() => clearRunBasis());

test('a live record ages against the reader clock', () => {
  assert.equal(formatRelative(ago('2026-09-12T11:30:00Z'), false, READING_NOW), '30m ago');
  assert.equal(formatRelative(ago('2026-09-12T06:00:00Z'), false, READING_NOW), '6hr ago');
  assert.equal(formatRelative(ago('2026-09-09T12:00:00Z'), false, READING_NOW), '3 days ago');
});

test('one day reads as a singular day', () => {
  assert.equal(formatRelative(ago('2026-09-11T12:00:00Z'), false, READING_NOW), '1 day ago');
});

test('a source clock running fast can never produce a time in the future', () => {
  // The pipeline clamps this on the way in; this is the display-side equivalent.
  assert.equal(formatRelative(ago('2026-09-12T12:05:00Z'), false, READING_NOW), 'just now');
  assert.equal(formatRelative(ago('2026-09-20T00:00:00Z'), false, READING_NOW), 'just now');
});

test('a demo record is aged against the recorded run, not against today', () => {
  setRunBasis(RUN_RECORDED_AT);

  // Six hours before the run was recorded.
  assert.equal(formatRelative(ago('2026-08-31T18:00:00Z'), true, READING_NOW), '6hr before the run');
});

/**
 * This is the guarantee the whole basis mechanism exists for. A synthetic event placed on a real
 * strait must not read as though it happened this afternoon, however long after the run the page
 * is opened — and the published snapshot is read for weeks after it is generated.
 */
test('a demo record reads identically however long after the run the page is opened', () => {
  setRunBasis(RUN_RECORDED_AT);

  const sameDay = formatRelative(ago('2026-08-31T18:00:00Z'), true, Date.parse('2026-09-01T01:00:00Z'));
  const elevenDaysLater = formatRelative(ago('2026-08-31T18:00:00Z'), true, READING_NOW);
  const nextYear = formatRelative(ago('2026-08-31T18:00:00Z'), true, Date.parse('2027-06-01T00:00:00Z'));

  assert.equal(sameDay, '6hr before the run');
  assert.equal(elevenDaysLater, sameDay);
  assert.equal(nextYear, sameDay);
});

test('a live record still ages against the reader clock while a run basis is set', () => {
  // The published snapshot carries both kinds at once, so the basis is chosen per record.
  setRunBasis(RUN_RECORDED_AT);

  assert.equal(formatRelative(ago('2026-09-12T06:00:00Z'), false, READING_NOW), '6hr ago');
});

test('a record at the moment of the run says so rather than saying just now', () => {
  setRunBasis(RUN_RECORDED_AT);

  assert.equal(formatRelative(RUN_RECORDED_AT, true, READING_NOW), 'at the start of the run');
});

test('an unparseable snapshot date leaves the basis alone rather than setting it to NaN', () => {
  assert.equal(setRunBasis('not a date'), false);
  assert.equal(setRunBasis(undefined), false);

  // Wrong, but bounded: it reads against the reader's clock instead of rendering "NaN days ago".
  assert.equal(formatRelative(ago('2026-09-12T06:00:00Z'), true, READING_NOW), '6hr ago');
});

test('a valid snapshot date is accepted and reported as accepted', () => {
  assert.equal(setRunBasis(RUN_RECORDED_AT), true);
  assert.deepEqual(relativeBasis(true, READING_NOW), {
    at: Date.parse(RUN_RECORDED_AT), suffix: 'before the run',
  });
});

test('with no basis set, both kinds of record fall back to the reader clock', () => {
  assert.deepEqual(relativeBasis(true, READING_NOW), { at: READING_NOW, suffix: 'ago' });
  assert.deepEqual(relativeBasis(false, READING_NOW), { at: READING_NOW, suffix: 'ago' });
});

test('a missing timestamp renders as nothing rather than as an epoch date', () => {
  assert.equal(formatRelative(null), '');
  assert.equal(formatRelative(undefined), '');
  assert.equal(formatRelative(''), '');
});

test('a source that stated no time of its own is recognised as having stated none', () => {
  // Normalisation falls back to the arrival time, leaving the two equal. Printing both would
  // present this system's own arrival time as though the source had reported it.
  const silent = { occurredAt: '2026-09-12T06:00:00Z', receivedAt: '2026-09-12T06:00:30Z' };
  const stated = { occurredAt: '2026-09-12T06:00:00Z', receivedAt: '2026-09-12T09:00:00Z' };

  assert.equal(statesOwnTime(silent), false);
  assert.equal(statesOwnTime(stated), true);
  assert.equal(statesOwnTime({ receivedAt: '2026-09-12T06:00:00Z' }), false);
});

test('absent times are named rather than rendered as a blank or an epoch', () => {
  assert.equal(formatTime(null), 'Unknown');
  assert.equal(formatDate(null), 'an unknown date');
  assert.equal(exactTimeHint(null), 'This source stated no time of its own.');
  assert.match(exactTimeHint('2026-09-12T06:00:00Z'), /^Source timestamp: .+/);
});
