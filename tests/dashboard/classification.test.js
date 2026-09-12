import { test } from 'node:test';
import assert from 'node:assert/strict';

import {
  confidenceChip, describeMethod, modelOpinion,
} from '../../src/Geopolitics.Api/wwwroot/lib/classification.js';

/**
 * These build markup as strings, and the strings are assigned through innerHTML. Every value that
 * originated outside this system is escaped on the way in; the tests that matter most here are the
 * ones that push a hostile value through and check that no markup comes out the other side.
 */

test('a method is described by whether a judgement was inferred, not by the vendor', () => {
  assert.equal(describeMethod('source-declared').kind, 'declared');
  assert.equal(describeMethod('source-declared').label, 'stated by source');
  assert.equal(describeMethod('source-declared+ai:claude').kind, 'mixed');
  assert.equal(describeMethod('source-declared+ai:claude').label, 'source + model');
  assert.equal(describeMethod('keyword').kind, 'heuristic');
  assert.equal(describeMethod('keyword').label, 'keyword match');
});

test('a model method names the model it used', () => {
  assert.deepEqual(describeMethod('ai:claude-opus-5'), {
    kind: 'model', label: 'claude-opus-5', detail: 'ai:claude-opus-5',
  });
});

test('an unrecorded method is named as unrecorded rather than left blank', () => {
  assert.equal(describeMethod(undefined).kind, 'unknown');
  assert.equal(describeMethod(undefined).label, 'unrecorded');
  assert.equal(describeMethod('').label, 'unrecorded');
});

test('the raw method is always carried through for the tooltip', () => {
  assert.equal(describeMethod('ai:some-model').detail, 'ai:some-model');
});

test('confidence is shown as a percentage beside the method that produced it', () => {
  const chip = confidenceChip(0.41, 'keyword');

  assert.match(chip, /41%/);
  assert.match(chip, /keyword match/);
  assert.match(chip, /conf-heuristic/);
});

test('a confidence that was never recorded is shown as unscored rather than as zero', () => {
  // Rendering "0%" would state a confidence the pipeline never expressed.
  for (const value of [0, -1, NaN, undefined, null, 'nonsense']) {
    const chip = confidenceChip(value, 'keyword');

    assert.match(chip, /unscored/, `${JSON.stringify(value)} should render as unscored`);
    assert.doesNotMatch(chip, /0%/);
  }
});

test('the chip says it is a confidence in a classification, not a probability of the event', () => {
  assert.match(confidenceChip(0.9, 'ai:model'), /not a probability that the event occurred/);
});

test('a hostile method string cannot inject markup through the chip', () => {
  const chip = confidenceChip(0.5, 'ai:"><script>alert(1)</script>');

  assert.doesNotMatch(chip, /<script/);
  assert.match(chip, /&lt;script&gt;/);
});

test('a hostile method string cannot break out of the title attribute', () => {
  // The raw method is interpolated into title="...", so the quote is the character that matters.
  const chip = confidenceChip(0.5, 'ai:" onmouseover="alert(1)');

  assert.doesNotMatch(chip, /title="[^"]*" onmouseover=/);
  assert.match(chip, /&quot;/);
});

test('an unscored chip escapes its method too', () => {
  const chip = confidenceChip(0, 'ai:<img src=x onerror=alert(1)>');

  assert.doesNotMatch(chip, /<img/);
});

test('no model opinion renders nothing at all', () => {
  assert.equal(modelOpinion(null, 'High'), '');
  assert.equal(modelOpinion(undefined, 'High'), '');
});

test('agreement is shown as plainly as disagreement', () => {
  // A panel that only appeared on disagreement would make disagreement look like an error state.
  const agreeing = modelOpinion(
    { severity: 'High', confidence: 0.82, modelVersion: 'sev-1', disagreesWithApplied: false }, 'High',
  );

  assert.match(agreeing, /agrees:/);
  assert.doesNotMatch(agreeing, /is-divergent/);
  assert.match(agreeing, /82%/);
});

test('disagreement names both severities and marks itself divergent', () => {
  const divergent = modelOpinion(
    { severity: 'Critical', confidence: 0.6, modelVersion: 'sev-1', disagreesWithApplied: true }, 'Medium',
  );

  assert.match(divergent, /is-divergent/);
  assert.match(divergent, /would have said/);
  assert.match(divergent, /Critical/);
  assert.match(divergent, /Medium/);
});

test('the opinion never claims to have set the severity', () => {
  const opinion = modelOpinion(
    { severity: 'High', confidence: 0.5, modelVersion: 'sev-1', disagreesWithApplied: false }, 'High',
  );

  assert.match(opinion, /never sets the severity an incident is stored with/);
});

test('an applied severity that was never recorded reads as Unknown rather than as blank', () => {
  const opinion = modelOpinion(
    { severity: 'High', confidence: 0.5, modelVersion: 'sev-1', disagreesWithApplied: true }, null,
  );

  assert.match(opinion, /Unknown/);
});

test('a hostile model version cannot inject markup', () => {
  const opinion = modelOpinion(
    {
      severity: 'High',
      confidence: 0.5,
      modelVersion: '"><script>alert(1)</script>',
      disagreesWithApplied: false,
    }, 'High',
  );

  assert.doesNotMatch(opinion, /<script/);
  assert.match(opinion, /&lt;script&gt;/);
});

test('a missing confidence renders as zero per cent rather than as NaN', () => {
  const opinion = modelOpinion(
    { severity: 'High', modelVersion: 'sev-1', disagreesWithApplied: false }, 'High',
  );

  assert.match(opinion, /0%/);
  assert.doesNotMatch(opinion, /NaN/);
});
