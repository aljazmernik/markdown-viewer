// Pure text helpers used by app.js. Kept free of DOM access so the Node tests
// (tests/web/text.test.mjs) can load this file directly.
(function (root) {
  // Inline markdown to plain text: links/images keep their label; emphasis, code marks and tags go.
  const stripInline = s => s
    .replace(/!?\[([^\]]*)\]\([^)]*\)/g, '$1')
    .replace(/[*_`~]/g, '')
    .replace(/<[^>]+>/g, '')
    .trim();

  // Number of lines taken by YAML front matter ("---" ... "---" or "..." at the very top), or 0.
  function frontMatterLines(lines) {
    if (!/^---\s*$/.test(lines[0] ?? '')) return 0;
    for (let i = 1; i < lines.length; i++) if (/^(---|\.\.\.)\s*$/.test(lines[i])) return i + 1;
    return 0;
  }

  // Headings from markdown source (editor modes): ATX "# x" and setext "x\n===", skipping code fences
  // and front matter (whose "key: value" line before the closing "---" would look like a heading).
  function headingsFromText(md) {
    const out = [], lines = md.split('\n');
    let fence = null;
    for (let i = frontMatterLines(lines); i < lines.length; i++) {
      const l = lines[i];
      const f = l.match(/^\s{0,3}(`{3,}|~{3,})/);
      if (f) { if (!fence) fence = f[1]; else if (f[1][0] === fence[0] && f[1].length >= fence.length) fence = null; continue; }
      if (fence) continue;
      const atx = l.match(/^\s{0,3}(#{1,6})\s+(.*?)(\s+#+)?\s*$/);
      if (atx) { out.push({ level: atx[1].length, text: stripInline(atx[2]), line: i + 1 }); continue; }
      const next = lines[i + 1];
      if (next !== undefined && l.trim() && (i === 0 || !lines[i - 1].trim()) && !/^\s*([-*+>]|\d+[.)])\s/.test(l)) {
        if (/^\s{0,3}=+\s*$/.test(next)) { out.push({ level: 1, text: stripInline(l), line: i + 1 }); i++; }
        else if (/^\s{0,3}-+\s*$/.test(next)) { out.push({ level: 2, text: stripInline(l), line: i + 1 }); i++; }
      }
    }
    return out;
  }

  // Words in a document, not counting fenced code, link targets or HTML tags.
  function countWords(md) {
    const lines = md.split('\n');
    const text = lines.slice(frontMatterLines(lines)).join('\n')
      .replace(/^(`{3,}|~{3,})[\s\S]*?^\1/gm, ' ')
      .replace(/!?\[([^\]]*)\]\([^)]*\)/g, '$1')
      .replace(/<[^>]+>/g, ' ');
    return (text.match(/[\p{L}\p{N}][\p{L}\p{N}'’_-]*/gu) || []).length;
  }

  // ---------- Visual mode round trip ----------
  // The WYSIWYG editor treats LaTeX as text and escapes it when saving (\pi -> \\pi, _ -> \_,
  // and "\," loses its backslash). So math goes in as code, which it keeps verbatim:
  // $$…$$ blocks as a "math-block" code block, inline $…$ as inline code starting with
  // MATH_MARK (invisible). restoreMath turns both back.
  const MATH_LANG = 'math-block', MATH_MARK = '⁣';
  const FENCE = /^\s{0,3}(`{3,}|~{3,})/;
  // Inline math like Pandoc: no space just inside the dollars, and no digit right after the
  // closing one, so "$5 and $10" stays text. $$…$$ on a line of text counts too.
  const INLINE_MATH = /(^|[^\\$])(\$\$?)(?=\S)((?:\\.|[^$\\\n])*?[^\s\\])\2(?!\d)/g;

  function protectMath(md) {
    const lines = md.split('\n'), out = [];
    let fence = null;
    for (let i = 0; i < lines.length; i++) {
      const l = lines[i], f = l.match(FENCE);
      if (fence) { out.push(l); if (f && f[1][0] === fence[0] && f[1].length >= fence.length) fence = null; continue; }
      if (f) { fence = f[1]; out.push(l); continue; }
      if (/^\s*\$\$\s*$/.test(l)) {
        const end = lines.findIndex((x, j) => j > i && /^\s*\$\$\s*$/.test(x));
        if (end > 0) { out.push('```' + MATH_LANG, ...lines.slice(i + 1, end), '```'); i = end; continue; }
      }
      // outside inline code spans only
      out.push(l.split(/(`+[^`]*`+)/).map((part, k) => k % 2 ? part
        : part.replace(INLINE_MATH, (m, pre, dollars, body) => `${pre}\`${MATH_MARK}${dollars}${body}${dollars}\``)).join(''));
    }
    return out.join('\n');
  }

  const restoreMath = md => md
    .replace(new RegExp('^```\\s*' + MATH_LANG + '\\s*\\n([\\s\\S]*?)\\n?```[ \\t]*$', 'gm'), '$$$$\n$1\n$$$$')
    .replace(new RegExp('`' + MATH_MARK + '(\\$\\$?)([^`]*?)\\1`', 'g'), '$1$2$1');

  // Toast UI's WYSIWYG serializer also escapes footnotes ("\[^1\]: text\.") and GitHub alert
  // markers ("> \[!NOTE\]"), which breaks them.
  const fixVisual = md => restoreMath(md)
    .replace(/\\\[\^([^\]\n]+?)\\\]/g, '[^$1]')
    .replace(/^(\[\^[^\]\n]+\]:.*)$/gm, line => line.replace(/\\\./g, '.'))
    .replace(/^(\s*>\s*)\\?\[!(NOTE|TIP|IMPORTANT|WARNING|CAUTION)\\?\]/gim, '$1[!$2]');

  // ---------- find & replace ----------

  // All matches of `query` in `text`: [{ start, end, groups }]. Returns null for an invalid
  // regular expression. Empty matches are skipped; at most `limit` are returned.
  function findAll(text, query, { matchCase = false, wholeWord = false, regex = false } = {}, limit = 10000) {
    if (!query) return [];
    let source = regex ? query : query.replace(/[.*+?^${}()|[\]\\]/g, '\\$&');
    if (wholeWord) source = `(?<![\\p{L}\\p{N}_])(?:${source})(?![\\p{L}\\p{N}_])`;
    let re;
    try { re = new RegExp(source, 'gu' + (matchCase ? '' : 'i')); } catch { return null; }
    const out = [];
    for (const m of text.matchAll(re)) {
      if (!m[0].length) continue;
      out.push({ start: m.index, end: m.index + m[0].length, groups: [...m] });
      if (out.length >= limit) break;
    }
    return out;
  }

  // The replacement text for one match: with `regex`, "$1".."$99", "$&" and "$$" work like in JavaScript.
  const expandReplacement = (match, replacement, regex) => !regex ? replacement
    : replacement.replace(/\$(\$|&|\d{1,2})/g, (s, g) => {
      if (g === '$') return '$';
      if (g === '&') return match.groups[0];
      if (+g < match.groups.length) return match.groups[+g] ?? '';
      if (g.length === 2 && +g[0] < match.groups.length) return (match.groups[+g[0]] ?? '') + g[1]; // "$10" with one group
      return s;
    });

  const api = { stripInline, frontMatterLines, headingsFromText, countWords, protectMath, restoreMath, fixVisual, findAll, expandReplacement };
  if (typeof module !== 'undefined' && module.exports) module.exports = api;
  else root.MdText = api;
})(typeof window !== 'undefined' ? window : globalThis);
