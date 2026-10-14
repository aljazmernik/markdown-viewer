// Tests for web/text.js. Run: node --test tests/web
import { test } from 'node:test';
import assert from 'node:assert/strict';
import { createRequire } from 'node:module';

const require = createRequire(import.meta.url);
const { headingsFromText, countWords, fixVisual, stripInline } = require('../../web/text.js');

test('outline: ATX headings with levels and line numbers', () => {
  const h = headingsFromText('# Title\n\ntext\n\n## Part A ##\n### Part *A.1*\n');
  assert.deepEqual(h, [
    { level: 1, text: 'Title', line: 1 },
    { level: 2, text: 'Part A', line: 5 },
    { level: 3, text: 'Part A.1', line: 6 },
  ]);
});

test('outline: setext headings', () => {
  const h = headingsFromText('Title\n=====\n\nIntro\n\nSub\n---\n');
  assert.deepEqual(h.map(x => [x.level, x.text]), [[1, 'Title'], [2, 'Sub']]);
});

test('outline: "#" lines inside code fences are not headings', () => {
  const md = '# Real\n\n```bash\n# comment\n```\n\n~~~\n## also code\n~~~\n\n## After\n';
  assert.deepEqual(headingsFromText(md).map(x => x.text), ['Real', 'After']);
});

test('outline: a "---" under a list item is not a setext heading', () => {
  assert.deepEqual(headingsFromText('- item\n---\n'), []);
});

test('outline: heading text keeps link labels and drops formatting', () => {
  assert.equal(stripInline('See [the docs](https://x.test) and `code` **now**'), 'See the docs and code now');
});

test('word count skips fenced code and link targets', () => {
  assert.equal(countWords('Hello world.\n\n```js\nconst notCounted = 1;\n```\n\n[two words](https://example.test/a/b)'), 4);
});

test('word count handles accents and apostrophes', () => {
  assert.equal(countWords("Čez cesto, don't stop — café"), 5);
});

test('Visual mode fix: un-escapes footnotes and their definitions', () => {
  const broken = 'Text.\\[^1\\]\n\n\\[^1\\]: The note\\.\n';
  assert.equal(fixVisual(broken), 'Text.[^1]\n\n[^1]: The note.\n');
});

test('Visual mode fix: leaves other escapes alone', () => {
  assert.equal(fixVisual('1\\. not a list\n'), '1\\. not a list\n');
});
