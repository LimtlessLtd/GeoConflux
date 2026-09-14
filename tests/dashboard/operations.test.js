import { test } from 'node:test';
import assert from 'node:assert/strict';

import {
  backupLine,
  downtimeLine,
  downtimeRows,
  formatBytes,
  growthLine,
  hasOperations,
  holdingRows,
  journalLine,
  retentionBoundary,
  retentionLine,
  spanLine,
  spatialScaleLine,
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
  retention: {
    enabled: false,
    keep: '90.00:00:00',
    prunableNow: 0,
    rowsRemoved: 0,
    reclaimedBytes: 0,
    lastRunAt: null,
    boundary: 'Evidence behind a published incident is never touched.',
    note: '',
  },
  downtime: {
    measurable: false,
    periods: [],
    sources: [],
    resolution: null,
    note: 'No source has ever polled on this host, so it cannot say when it was last running. '
      + 'That is not a statement that it has always been up.',
  },
  spatialScale: {
    searches: 12,
    truncated: 0,
    largestCandidateSet: 40,
    candidateCap: 1000,
    triggerReached: false,
    trigger: 'Move to PostGIS when a spatial search returns the full candidate cap.',
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
  assert.equal(retentionLine(null), '');
  assert.equal(retentionBoundary(null), '');
  assert.equal(downtimeLine(null), '');
  assert.deepEqual(downtimeRows(null), []);
  assert.equal(spatialScaleLine(null), '');
});

test('the spatial trigger speaks up only once the answers stop being complete', () => {
  // Below the cap the arrangement is exact and a standing line would be noise. Above it the panel
  // has to speak, because the failure is silent: a count reads as a count and is a cap.
  assert.equal(spatialScaleLine(report()), '');

  const tripped = spatialScaleLine(report({
    spatialScale: { ...report().spatialScale, truncated: 3, triggerReached: true },
  }));

  assert.match(tripped, /3 spatial searches returned the full 1000-row candidate cap/);
  assert.match(tripped, /floors rather than totals/);
  assert.match(tripped, /PostGIS/);
});

test('a host with nothing polling says it cannot tell, not that it never went down', () => {
  // The shipped state: every provider dormant, so nothing polls, so there is no record of uptime
  // either way. "No downtime" here would be the same error as an empty map reading as peace.
  const line = downtimeLine(report());

  assert.match(line, /cannot say when it was last running/);
  assert.match(line, /not a statement that it has always been up/);
});

test('recorded gaps render as intervals a recovery could be pointed at', () => {
  const rows = downtimeRows(report({
    downtime: {
      ...report().downtime,
      measurable: true,
      periods: [
        { startedAt: '2026-08-30T09:00:00Z', endedAt: '2026-09-13T09:00:00Z' },
        { startedAt: '2026-09-14T01:00:00Z', endedAt: '2026-09-14T04:00:00Z' },
      ],
    },
  }));

  assert.equal(rows.length, 2);
  assert.equal(rows[0].from, '2026-08-30 09:00');
  assert.equal(rows[0].length, '14 days');
  assert.equal(rows[1].length, '3 hours');
});

test('a malformed or inverted period is dropped rather than drawn as an invalid date', () => {
  const rows = downtimeRows(report({
    downtime: {
      ...report().downtime,
      periods: [
        { startedAt: 'not a date', endedAt: '2026-09-13T09:00:00Z' },
        { startedAt: '2026-09-14T09:00:00Z', endedAt: '2026-09-13T09:00:00Z' },
      ],
    },
  }));

  assert.deepEqual(rows, []);
});

test('the prunable count is stated whether or not retention is running', () => {
  // Off with nothing prunable and off while sitting on a hundred thousand prunable rows read
  // identically from a flag. The number is the only thing that separates them.
  const idle = retentionLine(report({
    retention: { ...report().retention, enabled: false, prunableNow: 100_000 },
  }));

  assert.match(idle, /Retention is off/);
  assert.match(idle, /100000 rows prunable today/);

  const running = retentionLine(report({
    retention: { ...report().retention, enabled: true, rowsRemoved: 400, prunableNow: 12 },
  }));

  assert.match(running, /400 rows removed since this host started/);
  assert.match(running, /12 rows prunable today/);
});

test('the boundary is shown only where deletion is actually happening', () => {
  // A standing reassurance printed where nothing is deleted is one readers learn to skip, which is
  // the day it would have mattered.
  assert.equal(retentionBoundary(report()), '');

  assert.match(
    retentionBoundary(report({ retention: { ...report().retention, enabled: true } })),
    /never touched/,
  );
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
