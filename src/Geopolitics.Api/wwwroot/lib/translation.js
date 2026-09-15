/**
 * Which text a reader sees, and what they are told about where it came from.
 *
 * The pipeline carries two versions of every report: the source's own words, and an English
 * rendering when something produced one. Neither is sufficient alone. A reader who only ever sees
 * English has no way to check a translated claim against what was actually published, and a reader
 * who only ever sees the original cannot read most of the feed — which is the state this module was
 * written to end.
 *
 * Nothing here decides whether a translation should happen. It decides which of the texts that exist
 * to show, and refuses to describe one as a translation unless the pipeline recorded that it is.
 */

import { escapeHtml } from './html.js';

/** The two ways to read the feed. Values are persisted, so they are strings rather than booleans. */
export const TEXT_MODE = Object.freeze({
  english: 'english',
  original: 'original',
});

/** Key under which the reader's choice is remembered between visits. */
export const TEXT_MODE_STORAGE_KEY = 'geoconflux.textMode';

/**
 * English is the default because the dashboard is written in English and most readers of it are
 * reading for content rather than provenance. The original is one click away and is never
 * discarded, which is the part that makes an English default defensible rather than lossy.
 */
export function normaliseMode(value) {
  return value === TEXT_MODE.original ? TEXT_MODE.original : TEXT_MODE.english;
}

/**
 * Whether the pipeline says a model rendered this observation into English.
 *
 * Read from the recorded state rather than inferred from the language tag. Inferring it is the
 * defect this replaces: a tag of "ar" was treated as proof that a translation had happened, when
 * the tag is usually copied off the feed at intake and the default provider translates nothing.
 */
export function isMachineTranslated(observation) {
  return observation?.translation === 'MachineTranslated';
}

/**
 * Whether the source published in a language other than English.
 *
 * Unknown counts as English here, and deliberately so: this drives a warning, and warning about
 * every record whose language nothing established would bury the cases where the language is known
 * and the reader genuinely cannot read it.
 */
export function isForeignLanguage(observation) {
  const tag = String(observation?.detectedLanguage ?? '').toLowerCase();
  return tag !== '' && tag !== 'en' && !tag.startsWith('en-');
}

/**
 * Whether there is English text this observation is missing.
 *
 * True is the honest reading of the common case on the published dashboard, where the deterministic
 * provider states plainly that it cannot translate. It is not an error, and it is not hidden.
 */
export function lacksTranslation(observation) {
  return isForeignLanguage(observation) && !isMachineTranslated(observation);
}

/**
 * The title and body to display, and an honest account of which language each is in.
 *
 * Returns the two texts separately rather than a single blob because they can disagree: a model may
 * render a headline and not a body, or the reverse, and a caller that assumed both moved together
 * would mislabel one of them.
 *
 * `titleIsOriginal` and `bodyIsOriginal` are reported rather than derived from the mode, for that
 * same reason — in English mode with nothing translated, both are still the source's own words.
 */
export function displayText(observation, mode) {
  const record = observation ?? {};
  const sourceTitle = text(record.title);
  const sourceBody = text(record.originalContent) ?? text(record.summary);

  if (normaliseMode(mode) === TEXT_MODE.original) {
    return {
      title: sourceTitle,
      body: sourceBody,
      titleIsOriginal: true,
      bodyIsOriginal: true,
    };
  }

  if (!isMachineTranslated(record)) {
    // Either the source wrote in English, in which case these already are the English, or nothing
    // translated it, in which case there is no English to show and the chip says so.
    return {
      title: sourceTitle,
      body: text(record.summary) ?? sourceBody,
      titleIsOriginal: true,
      bodyIsOriginal: true,
    };
  }

  const translatedTitle = text(record.translatedTitle);
  const translatedBody = text(record.translatedSummary);

  return {
    title: translatedTitle ?? sourceTitle,
    body: translatedBody ?? text(record.summary) ?? sourceBody,
    titleIsOriginal: translatedTitle === null,
    bodyIsOriginal: translatedBody === null,
  };
}

/**
 * What to say about this observation's language, or nothing when there is nothing worth saying.
 *
 * The three answers are different claims and are worded as such. "ar → en" asserts that a named
 * model produced the English on screen. "ar · not translated" admits that it did not. An English
 * source gets neither, because a chip on every record would make the language of a report look like
 * a caveat about it.
 */
export function languageNote(observation) {
  const record = observation ?? {};
  const tag = text(record.detectedLanguage);

  if (isMachineTranslated(record)) {
    const by = text(record.translationMethod);
    return {
      kind: 'translated',
      text: `${tag ?? 'source'} → en`,
      title: by
        ? `Translated into English by ${by}. A machine translation of the source text, not a verified rendering of it.`
        : 'Translated into English by the enrichment stage.',
    };
  }

  if (!isForeignLanguage(record)) {
    return null;
  }

  return {
    kind: 'untranslated',
    text: `${tag} · not translated`,
    title: `This report is in '${tag}' and nothing translated it, so the text shown is the source's own. `
      + 'The configured enrichment provider does not translate.',
  };
}

/** Renders {@link languageNote} as the chip the feed and the evidence list both use. */
export function languageChip(observation) {
  const note = languageNote(observation);
  if (!note) return '';

  return `<span class="lang-chip lang-${escapeHtml(note.kind)}" title="${escapeHtml(note.title)}">`
    + `${escapeHtml(note.text)}</span>`;
}

/**
 * The label for the control that switches modes.
 *
 * Phrased as the state being shown rather than the action, because a reader glancing at the header
 * needs to know which text is in front of them more than they need to know what the button does.
 */
export function modeLabel(mode) {
  return normaliseMode(mode) === TEXT_MODE.original
    ? { text: 'Source text', title: 'Showing each report in its original language, untranslated. Click to show English where it exists.' }
    : { text: 'English', title: 'Showing the English rendering where one was produced. Click to show every report in its original language.' };
}

/**
 * How many of the loaded observations the reader cannot read in English.
 *
 * Counted so the page can state the shortfall once, in the open, instead of leaving a reader to
 * infer it from a scattering of chips. A dashboard that reads ten languages and translates none of
 * them should say so.
 */
export function untranslatedCount(observations) {
  if (!Array.isArray(observations)) return 0;
  return observations.filter((observation) => lacksTranslation(observation)).length;
}

/** Trims to null, so an empty string from a feed and a missing field are one case rather than two. */
function text(value) {
  if (value === null || value === undefined) return null;
  const trimmed = String(value).trim();
  return trimmed === '' ? null : trimmed;
}
