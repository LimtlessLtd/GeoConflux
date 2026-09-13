import { test } from 'node:test';
import assert from 'node:assert/strict';

import {
  coverageSummary, gapNotes, hasCoverage, lexiconNote, orderByPrecision, precisionLabel,
} from '../../src/Geopolitics.Api/wwwroot/lib/coverage.js';

/**
 * The coverage panel exists so an empty patch of map stops being an implied claim. These assert the
 * wording, because the wording is the feature: "nothing placed" and "nothing happened" are the two
 * readings a reader can take from the same three dots, and only one of them is supportable.
 */

const theatre = (overrides = {}) => ({
  theatre: 'Tigray',
  placedCount: 0,
  byPrecision: [],
  bySource: [],
  gazetteerPlaces: 163,
  caveat: 'Coverage here is sparser than the conflict.',
  ...overrides,
});

test('an empty theatre is described as unreported rather than as quiet', () => {
  const summary = coverageSummary(theatre());

  // The distinction the whole panel exists for. A reader who takes "nothing placed" to mean
  // "nothing happened" has been misled by omission, so the sentence has to close that reading.
  assert.match(summary, /not about what happened/);
  assert.doesNotMatch(summary, /quiet|calm|no activity/i);
});

test('a covered theatre states its count and names its sources', () => {
  const summary = coverageSummary(theatre({
    placedCount: 12,
    bySource: [{ category: 'acled', count: 9 }, { category: 'ucdp', count: 3 }],
  }));

  assert.match(summary, /12 observations placed/);
  assert.match(summary, /acled, ucdp/);
});

test('a single observation is not described in the plural', () => {
  assert.match(coverageSummary(theatre({ placedCount: 1 })), /1 observation placed/);
});

test('sources contributing nothing are not named', () => {
  const summary = coverageSummary(theatre({
    placedCount: 4,
    bySource: [{ category: 'acled', count: 4 }, { category: 'ucdp', count: 0 }],
  }));

  assert.match(summary, /acled/);
  assert.doesNotMatch(summary, /ucdp/);
});

test('precision is ordered from most precise to least, not by size', () => {
  // The case that matters: the coarsest bucket is also the largest. Sorting by count would lead with
  // "to a country only" and bury the fact that almost nothing is precisely placed.
  const ordered = orderByPrecision([
    { category: 'Country', count: 40 },
    { category: 'Exact', count: 2 },
    { category: 'Region', count: 9 },
  ]);

  assert.deepEqual(ordered.map((entry) => entry.category), ['Exact', 'Region', 'Country']);
});

test('empty precision buckets are dropped rather than shown as zero', () => {
  const ordered = orderByPrecision([
    { category: 'Exact', count: 0 },
    { category: 'Settlement', count: 3 },
  ]);

  assert.deepEqual(ordered.map((entry) => entry.category), ['Settlement']);
});

test('an unrecognised precision sorts last and is described without inventing a meaning', () => {
  const ordered = orderByPrecision([
    { category: 'Somethingelse', count: 1 },
    { category: 'Exact', count: 1 },
  ]);

  assert.deepEqual(ordered.map((entry) => entry.category), ['Exact', 'Somethingelse']);
  assert.equal(precisionLabel('Somethingelse'), 'placed by an unstated method');
});

test('precision labels say what the reader gets, not what the enum is called', () => {
  assert.equal(precisionLabel('Settlement'), 'to a town');
  assert.equal(precisionLabel('Region'), 'to an area');
  assert.equal(precisionLabel('Country'), 'to a country only');
});

test('the lexicon ceiling is stated, because it explains why a theatre looks empty', () => {
  assert.match(lexiconNote(theatre({ gazetteerPlaces: 2105 })), /2,105 place names/);
});

test('a theatre with no place names says so plainly', () => {
  assert.match(lexiconNote(theatre({ gazetteerPlaces: 0 })), /no text report naming one can be drawn/i);
});

test('the panel shows even when every theatre is empty', () => {
  // That is the case it exists for, so an all-zero report must not be mistaken for nothing to show.
  assert.equal(hasCoverage({ theatres: [theatre(), theatre({ theatre: 'Yemen' })] }), true);
});

test('a missing or malformed report shows nothing rather than a broken panel', () => {
  assert.equal(hasCoverage(null), false);
  assert.equal(hasCoverage({}), false);
  assert.equal(hasCoverage({ theatres: [] }), false);
});

test('unresolved observations are reported with the reason they are not split by theatre', () => {
  const notes = gapNotes({
    unplacedCount: 7,
    unplacedNote: 'They are not split by theatre because an observation without a coordinate cannot be attributed to one.',
    ambiguousNameCount: 0,
  });

  assert.equal(notes.length, 1);
  assert.match(notes[0], /7 observations named a place that could not be resolved/);
  assert.match(notes[0], /cannot be attributed to one/);
});

test('names dropped for ambiguity are reported as a coverage limit', () => {
  const notes = gapNotes({ unplacedCount: 0, ambiguousNameCount: 536 });

  assert.equal(notes.length, 1);
  assert.match(notes[0], /536 place names are held but not used/);
  assert.match(notes[0], /wrong place/);
});

test('a report with no gaps claims none', () => {
  assert.deepEqual(gapNotes({ unplacedCount: 0, ambiguousNameCount: 0 }), []);
});

test('counts arriving as strings are coerced rather than concatenated', () => {
  // The payload is JSON from a static file, so a number that arrives as text must not turn "7" into
  // "70" or a count into NaN halfway through a sentence.
  assert.match(coverageSummary(theatre({ placedCount: '5' })), /5 observations placed/);
  assert.deepEqual(gapNotes({ unplacedCount: 'nonsense', ambiguousNameCount: 0 }), []);
});
