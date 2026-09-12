/**
 * The HTML-escaping primitive.
 *
 * This lives in a file of its own because of what depends on it. Several render paths build markup
 * as strings and assign it through innerHTML, so this function is the first half of the defence
 * against a hostile feed title reaching the DOM as markup; the second half is the content security
 * policy. A single character missing from the replacement set would open that path silently, which
 * is precisely the kind of regression a hand check does not catch on a refactor.
 */

/** Replaces the five characters that can break out of an attribute or a text node. */
export const escapeHtml = (value) => String(value ?? '').replace(/[&<>"']/g, (character) => ({
  '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;',
}[character]));
