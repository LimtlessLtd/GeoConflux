import { test } from 'node:test';
import assert from 'node:assert/strict';

import {
  baselineLine,
  coverageLine,
  orderConflicts,
  statesDirection,
  unassignedLine,
  verdictOf,
  volumeLine,
} from '../../src/Geopolitics.Api/wwwroot/lib/conflicts.js';

/**
 * The conflicts panel shows counts a reader will instinctively compare against each other and add
 * up, and both instincts are wrong here. These assert the wording that closes them, because the
 * wording is the feature.
 */

const tempo = (overrides = {}) => ({
  conflict: 'ucdp:333',
  name: 'Ethiopia: Tigray',
  current: { observations: 9, sources: 1, incidents: 4 },
  previous: { observations: 40, sources: 5, incidents: 18 },
  verdict: 'NotStated',
  statement: 'Coverage changed; tempo cannot be stated.',
  ...overrides,
});

test('a volume is never shown without the number of sources behind it', () => {
  // The denominator is the whole point. A count of reports moves when the world changes and equally
  // when the reporting does, and nothing in the figure itself distinguishes them.
  assert.equal(volumeLine({ observations: 9, sources: 2 }), '9 reports from 2 sources.');
  assert.equal(volumeLine({ observations: 1, sources: 1 }), '1 report from 1 source.');
});

test('an empty window says so rather than showing a zero', () => {
  assert.equal(volumeLine({ observations: 0, sources: 0 }), 'No reports this window.');
});

test('a verdict that is not a direction does not get an arrow', () => {
  // "Coverage changed" beside a downward arrow is the picture contradicting the sentence, and a
  // reader takes the picture.
  assert.equal(statesDirection('NotStated'), false);
  assert.equal(statesDirection('TooThin'), false);
  assert.equal(statesDirection('NoBaseline'), false);
  assert.equal(statesDirection('Steady'), false);

  assert.equal(statesDirection('Rising'), true);
  assert.equal(statesDirection('Falling'), true);
});

test('the coverage-changed verdict is labelled as being about coverage', () => {
  assert.equal(verdictOf('NotStated').label, 'Coverage changed');
  assert.equal(verdictOf('TooThin').label, 'Too thin to say');
});

test('an unrecognised verdict falls back to not stating anything', () => {
  // A future verdict this page has not been taught yet must not render as a direction, because the
  // safe default for an unknown claim is to make none.
  const unknown = verdictOf('SomethingNew');

  assert.equal(unknown.label, 'Not stated');
  assert.equal(statesDirection('SomethingNew'), false);
});

test('the baseline is this conflict own previous window', () => {
  assert.equal(baselineLine(tempo()), 'Previous window: 40 reports from 5 sources.');
});

test('a conflict with no previous window says so rather than showing an infinite rise', () => {
  const first = tempo({ previous: { observations: 0, sources: 0, incidents: 0 } });

  assert.match(baselineLine(first), /Nothing in the previous window/);
});

test('the headline states how much of the register was seen, with the register as the denominator', () => {
  const line = coverageLine({ seen: 12, registered: 319 });

  assert.match(line, /12 of the 319/);
  assert.match(line, /4%/);

  // And it says which of the two things the figure measures. Without this a reader reads it as a
  // count of the world's wars rather than as a count of this system's reach.
  assert.match(line, /this system's reach/);
});

test('a missing register is reported rather than rendered as complete coverage', () => {
  // Zero of zero is one hundred per cent. A page that computed that would claim total coverage at
  // the exact moment it had none.
  assert.match(coverageLine({ seen: 0, registered: 0 }), /No conflict register is loaded/);
});

test('reports nothing covers are counted apart from reports that did not say which', () => {
  const line = unassignedLine({ unassigned: 4, undecided: 7 });

  assert.match(line, /4 reports reached no conflict in the register/);
  assert.match(line, /7 reports fell inside the area/);
});

test('nothing unassigned produces no sentence at all', () => {
  assert.equal(unassignedLine({ unassigned: 0, undecided: 0 }), '');
});

test('conflicts are ordered by what was reported', () => {
  const ordered = orderConflicts([
    tempo({ conflict: 'a', current: { observations: 3, sources: 1, incidents: 1 } }),
    tempo({ conflict: 'b', current: { observations: 30, sources: 4, incidents: 9 } }),
  ]);

  assert.deepEqual(ordered.map((entry) => entry.conflict), ['b', 'a']);
});

test('missing counts are read as zero rather than as markup', () => {
  // The payload is fetched JSON, and every count on this page reaches innerHTML. A string that
  // arrived where a number was expected must become a number, not a fragment.
  assert.equal(volumeLine({ observations: '<img onerror=x>', sources: null }), 'No reports this window.');
});
