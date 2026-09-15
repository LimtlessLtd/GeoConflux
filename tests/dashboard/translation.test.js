import { test } from 'node:test';
import assert from 'node:assert/strict';

import {
  TEXT_MODE, displayText, isForeignLanguage, isMachineTranslated, lacksTranslation,
  languageChip, languageNote, modeLabel, normaliseMode, untranslatedCount,
} from '../../src/Geopolitics.Api/wwwroot/lib/translation.js';

/**
 * The defect these tests pin down: the page used to stamp "ar → en" on any observation whose
 * language was not English, whether or not anything had translated it. The language is normally
 * copied off the feed at intake, and the default provider translates nothing, so the label was
 * routinely a false claim about untranslated text sitting directly beneath it.
 */

const arabicUntranslated = {
  title: 'اشتباكات في المنطقة الساحلية',
  summary: '[Mock enrichment — not translated] The source text is in \'ar\'.',
  originalContent: 'اشتباكات في المنطقة الساحلية أسفرت عن إصابات.',
  detectedLanguage: 'ar',
  translation: 'NotTranslated',
};

const arabicTranslated = {
  title: 'اشتباكات في المنطقة الساحلية',
  summary: 'Clashes were reported in the coastal district.',
  originalContent: 'اشتباكات في المنطقة الساحلية أسفرت عن إصابات.',
  detectedLanguage: 'ar',
  translation: 'MachineTranslated',
  translatedTitle: 'Clashes in the coastal district',
  translatedSummary: 'Clashes were reported in the coastal district, with injuries.',
  translationMethod: 'ai:ollama/llama3.2',
};

const english = {
  title: 'Cargo vessel approached near Bab-el-Mandeb',
  summary: 'A cargo vessel was approached by small craft.',
  originalContent: 'A cargo vessel was approached by small craft. No injuries were reported.',
  detectedLanguage: 'en',
  translation: 'AlreadyEnglish',
};

test('an unrecognised mode falls back to English rather than to nothing', () => {
  assert.equal(normaliseMode(undefined), TEXT_MODE.english);
  assert.equal(normaliseMode('nonsense'), TEXT_MODE.english);
  assert.equal(normaliseMode(TEXT_MODE.original), TEXT_MODE.original);
});

test('a translation is read from the recorded state, never from the language tag', () => {
  // The whole defect in one assertion: foreign language, and no translation to go with it.
  assert.equal(isForeignLanguage(arabicUntranslated), true);
  assert.equal(isMachineTranslated(arabicUntranslated), false);
  assert.equal(isMachineTranslated(arabicTranslated), true);
});

test('an observation with no recorded translation state is not treated as translated', () => {
  assert.equal(isMachineTranslated({ detectedLanguage: 'ru' }), false);
  assert.equal(lacksTranslation({ detectedLanguage: 'ru' }), true);
});

test('an unknown language raises no warning, because unknown is not the same as unreadable', () => {
  assert.equal(isForeignLanguage({ detectedLanguage: null }), false);
  assert.equal(lacksTranslation({ detectedLanguage: null }), false);
});

test('a regional English tag counts as English', () => {
  assert.equal(isForeignLanguage({ detectedLanguage: 'en-GB' }), false);
  assert.equal(isForeignLanguage({ detectedLanguage: 'EN' }), false);
});

test('original mode shows the source text, whatever else exists', () => {
  const shown = displayText(arabicTranslated, TEXT_MODE.original);

  assert.equal(shown.title, arabicTranslated.title);
  assert.equal(shown.body, arabicTranslated.originalContent);
  assert.equal(shown.titleIsOriginal, true);
  assert.equal(shown.bodyIsOriginal, true);
});

test('English mode shows the translation when one was produced', () => {
  const shown = displayText(arabicTranslated, TEXT_MODE.english);

  assert.equal(shown.title, 'Clashes in the coastal district');
  assert.equal(shown.body, 'Clashes were reported in the coastal district, with injuries.');
  assert.equal(shown.titleIsOriginal, false);
  assert.equal(shown.bodyIsOriginal, false);
});

test('English mode does not pretend untranslated text is English', () => {
  const shown = displayText(arabicUntranslated, TEXT_MODE.english);

  assert.equal(shown.title, arabicUntranslated.title);
  assert.equal(shown.titleIsOriginal, true);
  assert.equal(shown.bodyIsOriginal, true);
});

test('a half-finished translation is reported as half finished', () => {
  // A model that rendered the body and skipped the headline leaves the headline in Arabic. Saying
  // otherwise would put a false label on the one line the feed actually displays.
  const partial = { ...arabicTranslated, translatedTitle: null };
  const shown = displayText(partial, TEXT_MODE.english);

  assert.equal(shown.title, partial.title);
  assert.equal(shown.titleIsOriginal, true);
  assert.equal(shown.bodyIsOriginal, false);
});

test('an English source reads the same in both modes', () => {
  const inEnglish = displayText(english, TEXT_MODE.english);
  const inOriginal = displayText(english, TEXT_MODE.original);

  assert.equal(inEnglish.title, inOriginal.title);
  assert.equal(inOriginal.body, english.originalContent);
});

test('the body falls back to the summary when no source content was published', () => {
  const shown = displayText({ title: 'A headline', summary: 'A summary.' }, TEXT_MODE.original);

  assert.equal(shown.body, 'A summary.');
});

test('a missing observation does not throw', () => {
  const shown = displayText(undefined, TEXT_MODE.english);

  assert.equal(shown.title, null);
  assert.equal(shown.body, null);
});

test('a translated observation names the model that translated it', () => {
  const note = languageNote(arabicTranslated);

  assert.equal(note.kind, 'translated');
  assert.equal(note.text, 'ar → en');
  assert.match(note.title, /ai:ollama\/llama3\.2/);
});

test('an untranslated foreign observation says so instead of claiming a translation', () => {
  const note = languageNote(arabicUntranslated);

  assert.equal(note.kind, 'untranslated');
  assert.equal(note.text, 'ar · not translated');
  assert.doesNotMatch(note.text, /→/);
});

test('an English source gets no language chip at all', () => {
  assert.equal(languageNote(english), null);
  assert.equal(languageChip(english), '');
});

test('a hostile language tag cannot inject markup through the chip', () => {
  const chip = languageChip({
    detectedLanguage: '"><script>alert(1)</script>',
    translation: 'NotTranslated',
  });

  assert.doesNotMatch(chip, /<script>/);
  assert.match(chip, /&lt;script&gt;/);
});

test('a hostile translation method cannot inject markup through the tooltip', () => {
  const chip = languageChip({
    ...arabicTranslated,
    translationMethod: '"><img src=x onerror=alert(1)>',
  });

  assert.doesNotMatch(chip, /<img/);
});

test('the mode label names the text being shown, not the action', () => {
  assert.equal(modeLabel(TEXT_MODE.english).text, 'English');
  assert.equal(modeLabel(TEXT_MODE.original).text, 'Source text');
});

test('the shortfall is counted so the page can state it once', () => {
  assert.equal(untranslatedCount([arabicUntranslated, arabicTranslated, english]), 1);
  assert.equal(untranslatedCount([]), 0);
  assert.equal(untranslatedCount(null), 0);
});
