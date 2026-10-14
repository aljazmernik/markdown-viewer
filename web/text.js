// Pure text helpers used by app.js. Kept free of DOM access so the Node tests
// (tests/web/text.test.mjs) can load this file directly.
(function (root) {
  // Inline markdown to plain text: links/images keep their label; emphasis, code marks and tags go.
  const stripInline = s => s
    .replace(/!?\[([^\]]*)\]\([^)]*\)/g, '$1')
    .replace(/[*_`~]/g, '')
    .replace(/<[^>]+>/g, '')
    .trim();

  // Headings from markdown source (editor modes): ATX "# x" and setext "x\n===", skipping code fences.
  function headingsFromText(md) {
    const out = [], lines = md.split('\n');
    let fence = null;
    for (let i = 0; i < lines.length; i++) {
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
    const text = md
      .replace(/^(`{3,}|~{3,})[\s\S]*?^\1/gm, ' ')
      .replace(/!?\[([^\]]*)\]\([^)]*\)/g, '$1')
      .replace(/<[^>]+>/g, ' ');
    return (text.match(/[\p{L}\p{N}][\p{L}\p{N}'’_-]*/gu) || []).length;
  }

  // Toast UI's WYSIWYG serializer escapes footnotes ("\[^1\]: text\."), which breaks them.
  const fixVisual = md => md
    .replace(/\\\[\^([^\]\n]+?)\\\]/g, '[^$1]')
    .replace(/^(\[\^[^\]\n]+\]:.*)$/gm, line => line.replace(/\\\./g, '.'));

  const api = { stripInline, headingsFromText, countWords, fixVisual };
  if (typeof module !== 'undefined' && module.exports) module.exports = api;
  else root.MdText = api;
})(typeof window !== 'undefined' ? window : globalThis);
