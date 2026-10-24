// Tests for web/text.js. Run: node --test tests/web
import { test } from 'node:test';
import assert from 'node:assert/strict';
import { createRequire } from 'node:module';

const require = createRequire(import.meta.url);
const { headingsFromText, countWords, fixVisual, stripInline, findAll, expandReplacement, protectMath, restoreMath } = require('../../web/text.js');

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

test('front matter: not an outline heading, not counted as words', () => {
  const md = '---\ntitle: My page\ntags: [a, b]\n---\n\n# Real heading\n\nOne two three.\n';
  assert.deepEqual(headingsFromText(md), [{ level: 1, text: 'Real heading', line: 6 }]);
  assert.equal(countWords(md), 5);
  // a thematic break later on is not front matter
  assert.equal(countWords('Intro\n\n---\n\nmore words\n'), 3);
});

test('visual fix: GitHub alert markers are unescaped', () => {
  assert.equal(fixVisual('> \\[!NOTE\\]\n> Useful.'), '> [!NOTE]\n> Useful.');
  assert.equal(fixVisual('> \\[!warning]\n> Careful.'), '> [!warning]\n> Careful.');
});

test('find: plain text, case, whole word', () => {
  const text = 'Cat cat concat CAT.';
  assert.deepEqual(findAll(text, 'cat').map(m => m.start), [0, 4, 11, 15]);
  assert.deepEqual(findAll(text, 'cat', { matchCase: true }).map(m => m.start), [4, 11]);
  assert.deepEqual(findAll(text, 'cat', { wholeWord: true }).map(m => m.start), [0, 4, 15]);
  assert.deepEqual(findAll('a.b axb', 'a.b').map(m => m.start), [0]); // dot is literal without regex
  assert.deepEqual(findAll(text, ''), []);
});

test('find: regular expressions, invalid patterns, replacement groups', () => {
  const m = findAll('2026-10-24 and 1999-01-02', '(\\d{4})-(\\d\\d)-(\\d\\d)', { regex: true });
  assert.equal(m.length, 2);
  assert.equal(expandReplacement(m[0], '$3.$2.$1', true), '24.10.2026');
  assert.equal(expandReplacement(m[0], '[$&] costs $$5', true), '[2026-10-24] costs $5');
  assert.equal(expandReplacement(m[0], '$3.$2.$1', false), '$3.$2.$1'); // literal without regex
  assert.equal(findAll('abc', '(', { regex: true }), null);
  assert.deepEqual(findAll('aaa', 'x*', { regex: true }), []); // empty matches are skipped
});
test('visual math: protected as code and restored exactly', () => {
  const md = 'Inline $e^{i\\pi} + 1 = 0$ and $$a_1$$ here.\n\n$$\n\\int_0^1 x^2\\,dx = \\frac{1}{3}\n$$\n\nCosts $5 and $10.\nEscaped \\$x$ too.\n\n`code $x$` stays.\n\n```\n$$\nnot math in a fence\n$$\n```\n';
  const p = protectMath(md);
  assert.ok(p.includes('```math-block\n\\int_0^1 x^2\\,dx = \\frac{1}{3}\n```'));
  assert.ok(p.includes('`\u2063$e^{i\\pi} + 1 = 0$`'));
  assert.ok(p.includes('`\u2063$$a_1$$`'));
  assert.ok(p.includes('Costs $5 and $10.'));            // prices are not math
  assert.ok(p.includes('`code $x$` stays.'));            // inline code untouched
  assert.ok(p.includes('```\n$$\nnot math in a fence\n$$\n```')); // fenced code untouched
  assert.equal(restoreMath(p), md);
  assert.equal(fixVisual(p), md);
});

test('visual math: Toast-style fence spacing is restored too', () => {
  assert.equal(restoreMath('``` math-block\nx^2\n```'), '$$\nx^2\n$$');
});