import test from 'node:test';
import assert from 'node:assert/strict';

import {
  attributionLine,
  attributionOf,
  claimChip,
  claimSummary,
  heldClaimNote,
  isClaim,
  isHeldClaim,
  platformsPresent,
} from '../../src/Geopolitics.Api/wwwroot/lib/attribution.js';

const wire = {
  sourceName: 'rss:bbc-world',
  tier: 'Published',
  status: 'Persisted',
};

const heldPost = {
  sourceName: 'collected:telegram/front_line_reports',
  tier: 'UserGenerated',
  platform: 'telegram',
  channel: 'front_line_reports',
  status: 'Uncorroborated',
};

const corroboratedPost = { ...heldPost, status: 'Persisted' };

const anonymous = {
  sourceName: 'manual:someone',
  tier: 'UserGenerated',
  platform: null,
  channel: null,
  status: 'Uncorroborated',
};

test('published reporting is reported by its publisher', () => {
  assert.equal(isClaim(wire), false);
  assert.equal(attributionLine(wire), 'Reported by rss:bbc-world');
});

test('a post is claimed on its channel, not reported by it', () => {
  // The grammar is the guarantee. "Reported by front_line_reports" would grant a handle the standing
  // of a newsroom in four words, and the whole tier exists because those are not the same thing.
  assert.equal(attributionLine(heldPost), 'Claimed on telegram/front_line_reports');
});

test('a handle carries its platform, because it is only unique within one', () => {
  const mastodon = { ...heldPost, platform: 'mastodon' };

  assert.notEqual(attributionOf(heldPost).by, attributionOf(mastodon).by);
});

test('an anonymous submission is named as what it is rather than given a channel', () => {
  assert.equal(attributionOf(anonymous).by, null);
  assert.equal(attributionLine(anonymous), 'Submitted anonymously through the open endpoint');
});

test('published reporting carries no claim chip', () => {
  // The ordinary case gets no furniture. A page of chips reads as noise and stops being read, which
  // would cost the chips that matter the attention they exist to command.
  assert.equal(claimChip(wire), null);
});

test('held and corroborated claims are different chips, not one chip restyled', () => {
  const held = claimChip(heldPost);
  const corroborated = claimChip(corroboratedPost);

  assert.match(held.text, /UNCORROBORATED/);
  assert.doesNotMatch(corroborated.text, /UNCORROBORATED/);
  assert.notEqual(held.text, corroborated.text);

  // Uncorroborated stops being true the moment a second source arrives, so a reader who learned to
  // skip the chip would be skipping the change.
  assert.match(held.title, /No independent source/);
  assert.match(corroborated.title, /independent source/);
});

test('a claim chip names the channel it came from', () => {
  assert.match(claimChip(heldPost).title, /telegram\/front_line_reports/);
});

test('the held note says what is unknown without calling the post false', () => {
  const note = heldClaimNote();

  assert.match(note, /posted, not because it is confirmed/);
  assert.doesNotMatch(note, /false|fake|untrue|unreliable/i);
});

test('a feed with no claims says nothing about claims', () => {
  assert.equal(claimSummary([wire, wire]), null);
});

test('the claim summary counts held claims separately', () => {
  const summary = claimSummary([wire, wire, heldPost, corroboratedPost]);

  assert.match(summary, /2 records of 4/);
  assert.match(summary, /1 of which is uncorroborated/);
});

test('a feed whose claims are all corroborated says so rather than staying silent', () => {
  const summary = claimSummary([wire, corroboratedPost]);

  assert.match(summary, /all corroborated/);
});

test('platforms present are counted busiest first', () => {
  const feed = [
    heldPost,
    corroboratedPost,
    { ...heldPost, platform: 'bluesky', channel: 'a.bsky.social' },
    wire,
    anonymous,
  ];

  assert.deepEqual(platformsPresent(feed), [
    { platform: 'telegram', count: 2 },
    { platform: 'bluesky', count: 1 },
  ]);
});

test('a payload written before tiers existed is treated as published reporting', () => {
  // The fallback runs this way round on purpose. Labelling old wire items as claims would be the
  // more misleading of the two errors and would be loud rather than quiet.
  assert.equal(isClaim({ sourceName: 'rss:old', status: 'Persisted' }), false);
  assert.equal(isHeldClaim({ sourceName: 'rss:old', status: 'Uncorroborated' }), false);
});

test('nothing here reads the record as absent', () => {
  assert.equal(isClaim(undefined), false);
  assert.equal(claimChip(null), null);
  assert.equal(attributionLine({}), 'Reported by an unnamed source');
  assert.deepEqual(platformsPresent(null), []);
});
