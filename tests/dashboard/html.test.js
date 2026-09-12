import { test } from 'node:test';
import assert from 'node:assert/strict';

import { escapeHtml } from '../../src/Geopolitics.Api/wwwroot/lib/html.js';

/**
 * Several render paths build markup as strings and assign it through innerHTML, so this function
 * is the first half of the defence against a hostile feed title reaching the DOM as markup. The
 * feeds this dashboard reads are public and uncontrolled: a title is attacker-influenced input.
 */

test('escapes every character that can break out of markup', () => {
  assert.equal(escapeHtml('&'), '&amp;');
  assert.equal(escapeHtml('<'), '&lt;');
  assert.equal(escapeHtml('>'), '&gt;');
  assert.equal(escapeHtml('"'), '&quot;');
  assert.equal(escapeHtml("'"), '&#39;');
});

test('a script tag in a feed title cannot become an element', () => {
  const escaped = escapeHtml('<script>alert(1)</script>');

  assert.equal(escaped, '&lt;script&gt;alert(1)&lt;/script&gt;');
  assert.doesNotMatch(escaped, /<script/);
});

test('an event handler payload cannot close the tag it is interpolated into', () => {
  const escaped = escapeHtml('<img src=x onerror=alert(1)>');

  assert.doesNotMatch(escaped, /</);
  assert.doesNotMatch(escaped, />/);
});

test('quotes are escaped, so interpolation into an attribute cannot escape the attribute', () => {
  // confidenceChip and modelOpinion both interpolate into title="...", which single-character
  // escaping of angle brackets alone would not protect.
  const escaped = escapeHtml('" onmouseover="alert(1)');

  assert.doesNotMatch(escaped, /"/);
  assert.equal(escaped, '&quot; onmouseover=&quot;alert(1)');
});

test('an ampersand is escaped first, so an entity cannot be smuggled through', () => {
  // If & were replaced after <, the string "&lt;" would arrive as a literal "<" in the DOM.
  assert.equal(escapeHtml('&lt;script&gt;'), '&amp;lt;script&amp;gt;');
});

test('absent values become the empty string rather than the words null or undefined', () => {
  assert.equal(escapeHtml(null), '');
  assert.equal(escapeHtml(undefined), '');
});

test('non-string values are coerced rather than crashing a render', () => {
  assert.equal(escapeHtml(0), '0');
  assert.equal(escapeHtml(false), 'false');
  assert.equal(escapeHtml(42), '42');
});

test('safe text is returned unchanged', () => {
  assert.equal(escapeHtml('Red Sea shipping lane'), 'Red Sea shipping lane');
});
