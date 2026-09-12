import { test } from 'node:test';
import assert from 'node:assert/strict';

import {
  SEVERITY_COLOUR, asCount, colourFor, humanise, plural,
} from '../../src/Geopolitics.Api/wwwroot/lib/format.js';

test('an unrecognised severity still gets a colour rather than undefined', () => {
  assert.equal(colourFor('Critical'), SEVERITY_COLOUR.Critical);
  assert.equal(colourFor('Nonsense'), SEVERITY_COLOUR.Unknown);
  assert.equal(colourFor(undefined), SEVERITY_COLOUR.Unknown);
});

test('every severity has a distinct colour', () => {
  const colours = Object.values(SEVERITY_COLOUR);

  assert.equal(new Set(colours).size, colours.length);
});

/**
 * asCount exists so a snapshot-supplied value cannot carry markup into innerHTML. The counts are
 * interpolated unescaped, on the strength of this coercion.
 */
test('a count that is not a number becomes zero rather than reaching the DOM', () => {
  assert.equal(asCount('<img src=x onerror=alert(1)>'), 0);
  assert.equal(asCount('12; DROP TABLE'), 0);
  assert.equal(asCount(undefined), 0);
  assert.equal(asCount(null), 0);
  assert.equal(asCount(NaN), 0);
  assert.equal(asCount(Infinity), 0);
});

test('a genuine count survives coercion, including as a string', () => {
  assert.equal(asCount(7), 7);
  assert.equal(asCount('7'), 7);
  assert.equal(asCount(0), 0);
});

test('counts are always numbers, so they can never carry markup', () => {
  for (const value of ['<b>1</b>', {}, [], 'abc', () => 1]) {
    assert.equal(typeof asCount(value), 'number');
  }
});

test('one of something reads in the singular', () => {
  assert.equal(plural(1, 'day'), '1 day');
  assert.equal(plural(2, 'day'), '2 days');
  assert.equal(plural(0, 'day'), '0 days');
});

test('identifiers are turned into prose rather than shown raw', () => {
  assert.equal(humanise('MARITIME_INCIDENT'), 'Maritime incident');
  assert.equal(humanise('MaritimeIncident'), 'Maritime incident');
  assert.equal(humanise('ARMED_CONFLICT'), 'Armed conflict');
});

test('an absent value becomes a dash rather than an empty gap', () => {
  assert.equal(humanise(null), '—');
  assert.equal(humanise(undefined), '—');
  assert.equal(humanise(''), '—');
  assert.equal(humanise('   '), '—');
});
