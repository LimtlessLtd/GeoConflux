/**
 * What the page may say about the host behind it.
 *
 * Every other panel describes the world. This one describes the machine: how many rows it is
 * holding, how much disk that is, and over what span it accumulated. It is on the coverage tab
 * because it is the same kind of statement — a bound on what the rest of the page can be — and it
 * matters most in the case that looks least interesting. A snapshot build holds a database that was
 * created seconds earlier and is deleted with the job, and saying that plainly is the difference
 * between a demonstration and a claim about a system that has been watching.
 *
 * Pure, like the rest of lib: the wording is asserted directly rather than hunted for in a browser.
 */

import { asCount, plural } from './format.js';

/** Binary units, because that is what a filesystem reports and what an operator will compare against. */
const UNITS = ['bytes', 'KiB', 'MiB', 'GiB', 'TiB'];

/**
 * A byte count a person can read.
 *
 * Kept to one decimal above the kibibyte, because the second one is never the reason anybody looks
 * at this number. Zero is "0 bytes" rather than "empty": a database with no rows still has pages.
 */
export function formatBytes(value) {
  const bytes = asCount(value);
  if (bytes < 1024) return `${Math.round(bytes)} bytes`;

  let size = bytes;
  let unit = 0;

  while (size >= 1024 && unit < UNITS.length - 1) {
    size /= 1024;
    unit += 1;
  }

  return `${size.toFixed(1)} ${UNITS[unit]}`;
}

/** False only when the payload is absent or malformed; an empty database is worth showing. */
export function hasOperations(report) {
  return Array.isArray(report?.holdings?.tables);
}

/**
 * The row counts, largest first, as the server ordered them.
 *
 * Ordering is not re-decided here. The server put the largest table first because that is the one a
 * retention decision is about, and a client that re-sorted by name would bury it.
 */
export function holdingRows(report) {
  return (report?.holdings?.tables ?? []).map((table) => ({
    table: String(table?.table ?? ''),
    rows: asCount(table?.rows),
    holds: String(table?.holds ?? ''),
  }));
}

/**
 * One sentence on disk.
 *
 * The journal and the reclaimable figure are only mentioned when they are non-zero. A host whose
 * write-ahead log is empty and which has deleted nothing does not need two clauses saying so, and
 * printing "0 bytes reclaimable" trains a reader to skip the line on the day it is not zero.
 */
export function storageLine(report) {
  const storage = report?.holdings?.storage;
  if (!storage) return '';

  const parts = [`${formatBytes(storage.databaseBytes)} stored`];

  if (asCount(storage.journalBytes) > 0) {
    parts.push(`${formatBytes(storage.journalBytes)} in the write-ahead log`);
  }

  if (asCount(storage.reclaimableBytes) > 0) {
    parts.push(`${formatBytes(storage.reclaimableBytes)} freed but not yet returned`);
  }

  return `${parts.join(', ')}.`;
}

/** The journal mode, said plainly, because a long-running host wants one of them and not the other. */
export function journalLine(report) {
  const mode = String(report?.holdings?.storage?.journalMode ?? '').toLowerCase();
  if (!mode) return '';

  return mode === 'wal'
    ? 'Write-ahead logging is on, so a reader and a writer do not block each other.'
    : `Journal mode is ${mode}; a continuously-running host wants wal.`;
}

/**
 * How long this database has been accumulating.
 *
 * The short answer is the one that matters. A span of seconds means this is a build's own database
 * rather than a host that has been running, and the panel should say so in those words rather than
 * leave a reader to notice two timestamps are close together.
 */
export function spanLine(report) {
  const from = report?.holdings?.oldestRecordAt;
  const to = report?.holdings?.newestRecordAt;

  if (!from || !to) return 'This database holds no observations.';

  const seconds = Math.round((Date.parse(to) - Date.parse(from)) / 1000);

  if (!Number.isFinite(seconds)) return 'This database holds no observations.';

  if (seconds < 600) {
    return 'Every record here arrived within ten minutes of the last, which is what a snapshot '
      + 'build looks like: the database was created for this run and is discarded with it.';
  }

  const days = Math.round(seconds / 86400);
  if (days >= 1) return `Records span ${plural(days, 'day')}.`;

  const hours = Math.max(1, Math.round(seconds / 3600));
  return `Records span ${plural(hours, 'hour')}.`;
}

/**
 * Whether any of this would survive the disk it is on.
 *
 * The server writes the judgement, not the client: the three cases — no destination, a destination
 * with nothing in it, and copies on the same volume as the original — are different facts, and the
 * middle one looks exactly like the third from a count alone. This passes it through rather than
 * re-deriving it from a boolean, which is how the distinction would get lost.
 */
export function backupLine(report) {
  const backups = report?.backups;
  if (!backups) return '';

  return String(backups.note ?? '');
}

/**
 * What a year at the current rate would cost, or why that was not answered.
 *
 * The refusal is passed through rather than replaced with a zero. A projection declined for want of
 * history is a different statement from a projection of nothing, and only one of them tells a
 * reader to come back later.
 */
export function growthLine(report) {
  const growth = report?.holdings?.growth;
  if (!growth) return '';

  const week = asCount(growth.observationsLastWeek);
  const arrived = `${plural(week, 'observation')} in the last seven days`;

  return growth.projectedYearBytes === null || growth.projectedYearBytes === undefined
    ? `${arrived}. Growth is not projected yet: this database holds less than seven days of records.`
    : `${arrived}, which projects to ${formatBytes(growth.projectedYearBytes)} a year at this rate.`;
}
