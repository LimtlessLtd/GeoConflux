import { test } from 'node:test';
import assert from 'node:assert/strict';

import {
  backupLine,
  formatBytes,
  growthLine,
  hasOperations,
  holdingRows,
  journalLine,
  spanLine,
  storageLine,
} from '../../src/Geopolitics.Api/wwwroot/lib/operations.js';

/**
 * The operations panel is the one place the page describes itself rather than the world. Two of its
 * statements are load-bearing and both are refusals: a projection declined for want of history, and
 * a span of seconds admitted to be a build rather than presented as a running host. The wording is
 * asserted because the wording is the feature.
 */

const report = (overrides = {}) => ({
  holdings: {
    tables: [
      { table: 'observations', rows: 24, holds: 'what sources actually said' },
      { table: 'incidents', rows: 9, holds: 'what this system concluded from them' },
    ],
    storage: {
      databaseBytes: 4_096_000,
      journalBytes: 0,
      reclaimableBytes: 0,
      journalMode: 'wal',
      note: '',
    },
    oldestRecordAt: '2026-09-01T00:00:00Z',
    newestRecordAt: '2026-09-14T00:00:00Z',
    growth: {
      observationsLastWeek: 140,
      bytesPerObservation: 170_666,
      projectedYearBytes: 1_243_000_000,
      basis: '',
    },
    note: '',
  },
  backups: {
    configured: false,
    copies: 0,
    newestAt: null,
    newestBytes: 0,
    note: 'No backup destination is configured, so nothing here would survive the loss of this database.',
  },
  measuredAt: '2026-09-14T12:00:00Z',
  ...overrides,
});

const holdings = (overrides) => report({ holdings: { ...report().holdings, ...overrides } });

test('bytes are reported in units an operator compares against', () => {
  assert.equal(formatBytes(0), '0 bytes');
  assert.equal(formatBytes(512), '512 bytes');
  assert.equal(formatBytes(1024), '1.0 KiB');
  assert.equal(formatBytes(1024 * 1024 * 3.5), '3.5 MiB');
  assert.equal(formatBytes(1024 ** 3), '1.0 GiB');
});

test('a malformed or missing payload leaves the panel empty rather than throwing', () => {
  assert.equal(hasOperations(null), false);
  assert.equal(hasOperations({}), false);
  assert.equal(hasOperations({ holdings: { tables: [] } }), true);
  assert.deepEqual(holdingRows(null), []);
  assert.equal(storageLine(null), '');
  assert.equal(growthLine(null), '');
  assert.equal(journalLine(null), '');
  assert.equal(backupLine(null), '');
});

test('the backup statement is passed through rather than rebuilt from a flag', () => {
  // Three cases the server distinguishes and a boolean cannot: nothing configured, a destination
  // that has never been written to, and copies sitting on the same disk as the original.
  assert.match(backupLine(report()), /would survive the loss/);

  assert.equal(
    backupLine(report({ backups: { configured: true, copies: 7, note: 'They are on the same volume.' } })),
    'They are on the same volume.',
  );
});

test('row counts keep the order the server chose', () => {
  // Largest first is a decision about what a retention question is about. Re-sorting by name in the
  // client would bury the only row anybody is deciding against.
  const rows = holdingRows(report());

  assert.deepEqual(rows.map((row) => row.table), ['observations', 'incidents']);
  assert.equal(rows[0].rows, 24);
});

test('a count that is not a number cannot carry markup into the page', () => {
  const rows = holdingRows(holdings({
    tables: [{ table: 'observations', rows: '<img onerror=alert(1)>', holds: '' }],
  }));

  assert.equal(rows[0].rows, 0);
});

test('storage mentions the journal and freed space only when there is any', () => {
  assert.equal(storageLine(report()), '3.9 MiB stored.');

  const busy = storageLine(holdings({
    storage: { ...report().holdings.storage, journalBytes: 2_097_152, reclaimableBytes: 1_048_576 },
  }));

  assert.match(busy, /write-ahead log/);
  assert.match(busy, /freed but not yet returned/);
});

test('a journal mode other than wal is named as the wrong one for a host left running', () => {
  assert.match(journalLine(report()), /do not block each other/);

  const rollback = journalLine(holdings({
    storage: { ...report().holdings.storage, journalMode: 'delete' },
  }));

  assert.match(rollback, /journal mode is delete/i);
  assert.match(rollback, /wants wal/);
});

test('a database whose records all arrived at once says it is a build, not a watched month', () => {
  // The published page runs against a database created for that build and deleted with it. Reading
  // its row counts as the holdings of a system that has been watching would be the single most
  // misleading thing this panel could do, so the short span is called what it is.
  const line = spanLine(holdings({
    oldestRecordAt: '2026-09-14T12:00:00Z',
    newestRecordAt: '2026-09-14T12:00:30Z',
  }));

  assert.match(line, /snapshot build/);
  assert.match(line, /discarded with it/);
});

test('a real span is reported in days', () => {
  assert.equal(spanLine(report()), 'Records span 13 days.');
});

test('an empty database says so rather than inventing a span', () => {
  assert.match(spanLine(holdings({ oldestRecordAt: null, newestRecordAt: null })), /no observations/);
});

test('growth declines to project rather than multiplying an hour by a year', () => {
  // The same rule the narrative evidence floor applies to prose. A figure with a year's authority
  // and an hour's support is worse than no figure, because it will be quoted.
  const line = growthLine(holdings({
    growth: { ...report().holdings.growth, projectedYearBytes: null },
  }));

  assert.match(line, /not projected yet/);
  assert.match(line, /less than seven days/);
  assert.doesNotMatch(line, /a year at this rate/);
});

test('growth states the week it was measured over beside the year it projects', () => {
  const line = growthLine(report());

  assert.match(line, /140 observations in the last seven days/);
  assert.match(line, /a year at this rate/);
});
