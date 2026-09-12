import { test } from 'node:test';
import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';
import { fileURLToPath, pathToFileURL } from 'node:url';
import { dirname, resolve } from 'node:path';

/**
 * Splitting the client into modules introduced a failure mode a single file did not have: a
 * renamed export or a file missing from the published output breaks the page outright, and it
 * breaks it silently, because the browser reports it in a console nobody is watching.
 *
 * These tests read the real app.js and check that every import it makes actually resolves.
 */

const here = dirname(fileURLToPath(import.meta.url));
const wwwroot = resolve(here, '../../src/Geopolitics.Api/wwwroot');

const readWwwroot = (name) => readFile(resolve(wwwroot, name), 'utf8');

/** Matches both the single-line and the braced multi-line import forms app.js uses. */
const importsOf = (source) => [...source.matchAll(/import\s*\{([\s\S]*?)\}\s*from\s*'([^']+)'/g)]
  .map(([, names, specifier]) => ({
    specifier,
    names: names.split(',').map((name) => name.trim()).filter(Boolean)
      // "resolveDataSource as resolveSourceOrder" is imported under a local alias.
      .map((name) => name.split(/\s+as\s+/)[0].trim()),
  }));

test('app.js imports only from its own lib directory', async () => {
  const source = await readWwwroot('app.js');
  const specifiers = importsOf(source).map((each) => each.specifier);

  assert.ok(specifiers.length > 0, 'app.js should import the logic that was extracted from it');
  for (const specifier of specifiers) {
    assert.match(specifier, /^\.\/lib\/[a-z]+\.js$/, `unexpected import specifier: ${specifier}`);
  }
});

test('every module app.js imports exists and exports what it asks for', async () => {
  const source = await readWwwroot('app.js');

  for (const { specifier, names } of importsOf(source)) {
    const path = resolve(wwwroot, specifier);
    const module = await import(pathToFileURL(path).href);

    for (const name of names) {
      assert.equal(
        typeof module[name] !== 'undefined', true,
        `app.js imports ${name} from ${specifier}, which does not export it`,
      );
    }
  }
});

test('the page loads app.js as a module, without which every import fails', async () => {
  const html = await readWwwroot('index.html');

  assert.match(html, /<script\s+type="module"\s+src="\.\/app\.js"><\/script>/);
});

test('every script and style the page references is a relative path', async () => {
  // The site is served from a subpath on GitHub Pages, so a leading "/" would resolve to the
  // domain root and break the request.
  const html = await readWwwroot('index.html');
  const local = [...html.matchAll(/(?:src|href)="(?!https?:|data:|#)([^"]+)"/g)].map(([, path]) => path);

  assert.ok(local.length > 0);
  for (const path of local) {
    assert.ok(path.startsWith('./'), `${path} should be relative to the page, not to the domain root`);
  }
});

test('the content policy still allows the page to load its own scripts', async () => {
  const html = await readWwwroot('index.html');
  const policy = html.match(/Content-Security-Policy"\s+content="([\s\S]*?)"/)?.[1];

  assert.ok(policy, 'the page should declare a content security policy');
  assert.match(policy, /script-src[^;]*'self'/);
});

/**
 * The checks above read app.js as text. This one actually loads it.
 *
 * A module graph can satisfy every static check and still fail to bind — a circular import, or an
 * export that exists but is not initialised when another module reads it, both surface only on
 * execution. Under Node the file must therefore get as far as reaching for a DOM that is not there:
 * that failure proves every import resolved and bound. A syntax error or a missing export would
 * mean the published page breaks before it runs a line of its own.
 */
test('app.js binds its whole import graph, failing only for want of a browser', async () => {
  const appjs = pathToFileURL(resolve(wwwroot, 'app.js')).href;

  await assert.rejects(
    () => import(appjs),
    (error) => {
      assert.notEqual(error.constructor.name, 'SyntaxError', `app.js does not parse: ${error.message}`);
      assert.doesNotMatch(
        error.message, /does not provide an export|Cannot find module|ERR_MODULE_NOT_FOUND/,
        `app.js has an unresolved import: ${error.message}`,
      );

      // What is left is the expected one: the file reached for the DOM, which Node does not have.
      assert.match(error.message, /document is not defined/);
      return true;
    },
  );
});
