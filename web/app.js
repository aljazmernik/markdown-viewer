(() => {
  const $ = id => document.getElementById(id);
  const init = JSON.parse($('init').textContent);
  const C = $('content');
  const darkQuery = matchMedia('(prefers-color-scheme: dark)');
  let dark = darkQuery.matches;
  // This page runs in a tab iframe; the shell (parent) relays messages to the app.
  const post = msg => parent.shell.fromDoc(init.id, msg);

  // ---------- bundled libraries, loaded on demand ----------
  const LIB = '/__res/lib/';
  const cache = {};
  const load = src => cache[src] ??= new Promise((ok, fail) => {
    const s = document.createElement('script');
    s.src = LIB + src; s.onload = ok; s.onerror = fail;
    document.head.append(s);
  });
  const css = href => {
    if (document.querySelector(`link[data-lib="${href}"]`)) return;
    const l = document.createElement('link');
    l.rel = 'stylesheet'; l.href = LIB + href; l.dataset.lib = href;
    document.head.append(l);
  };

  async function enhance() {
    const codes = C.querySelectorAll('pre > code');
    if (codes.length) try {
      await load('hljs/highlight.min.js');
      codes.forEach(c => { if (!c.closest('.mermaid')) hljs.highlightElement(c); });
    } catch {}
    const diagrams = C.querySelectorAll('.mermaid');
    if (diagrams.length) try {
      await load('mermaid/mermaid.min.js');
      mermaid.initialize({ startOnLoad: false, theme: dark ? 'dark' : 'default' });
      await mermaid.run({ nodes: diagrams });
    } catch {}
    if (C.querySelector('.math')) try {
      css('katex/katex.min.css');
      await load('katex/katex.min.js');
      await load('katex/contrib/auto-render.min.js');
      renderMathInElement(C, { throwOnError: false, delimiters: [
        { left: '\\[', right: '\\]', display: true }, { left: '\\(', right: '\\)', display: false },
        { left: '$$', right: '$$', display: true } ] });
    } catch {}
  }

  // ---------- state ----------
  let doc = init.md;          // markdown on disk, or the latest edited text
  let renderedMd = init.md;   // markdown the preview HTML was rendered from
  let mode = 'preview', lastEditMode = 'markdown';
  let editor = null, suppress = 0, dirty = false, pending = null;

  // Visual mode re-serializes the whole document in its own style. To keep the
  // user's exact source, `rawMd` holds the text as it was when Visual mode was
  // entered, and is used unless something was actually edited there.
  let rawMd = init.md, visualEdited = false;

  // Toast UI's WYSIWYG serializer escapes footnotes ("\[^1\]: text\."), which breaks them.
  const fixVisual = md => md
    .replace(/\\\[\^([^\]\n]+?)\\\]/g, '[^$1]')
    .replace(/^(\[\^[^\]\n]+\]:.*)$/gm, line => line.replace(/\\\./g, '.'));

  function currentMarkdown() {
    if (!editor) return doc;
    if (!editor.isWysiwygMode()) return editor.getMarkdown();
    return visualEdited ? fixVisual(editor.getMarkdown()) : rawMd;
  }

  // Make `md` the baseline for the Visual editor (after save / leaving it).
  function rebaseVisual(md) { rawMd = md; visualEdited = false; }

  function setDirty(v) {
    if (dirty === v) return;
    dirty = v;
    $('save').disabled = !v;
    document.body.classList.toggle('dirty', v);
    post({ type: 'dirty', value: v });
  }

  // Run editor calls that fire 'change' without counting them as user edits.
  function quiet(fn) {
    suppress++;
    try { fn(); } finally { setTimeout(() => suppress--, 0); }
  }

  async function ensureEditor(type) {
    if (editor) return;
    css('toastui/toastui-editor.css');
    css('toastui/toastui-editor-dark.css'); // scoped to .toastui-editor-dark, so harmless in light mode
    await load('toastui/toastui-editor-all.min.js');
    quiet(() => {
      editor = new toastui.Editor({
        el: $('editor'), height: '100%', initialEditType: type, previewStyle: 'vertical',
        initialValue: doc, usageStatistics: false, hideModeSwitch: true, theme: dark ? 'dark' : 'light',
      });
    });
    editor.on('change', () => {
      scheduleRefresh();
      if (suppress) return;
      if (editor.isWysiwygMode()) visualEdited = true;
      setDirty(true);
    });
    editor.on('caretChange', updateCaret);
  }

  async function setMode(m) {
    if (m === mode || (m !== 'preview' && !init.editable)) return;
    mode = m;
    document.querySelectorAll('[data-mode]').forEach(b => b.classList.toggle('on', b.dataset.mode === m));

    if (m === 'preview') {
      if (editor && dirty) { doc = currentMarkdown(); rebaseVisual(doc); }
      $('editor').hidden = true; $('preview').hidden = false;
      if (doc !== renderedMd) { renderedMd = doc; post({ type: 'render', md: doc }); }
      else refreshOutline();
      updateStatus();
      return;
    }

    lastEditMode = m;
    const toVisual = m === 'visual';
    $('preview').hidden = true; $('editor').hidden = false;
    if (!editor) {
      rebaseVisual(doc);
      await ensureEditor(toVisual ? 'wysiwyg' : 'markdown');
    } else if (editor.isWysiwygMode() !== toVisual) {
      const source = currentMarkdown();
      rebaseVisual(source);
      quiet(() => {
        editor.changeMode(toVisual ? 'wysiwyg' : 'markdown', true);
        if (!toVisual) editor.setMarkdown(source, false); // exact text, not Visual's re-serialization
      });
    }
    editor.focus();
    refreshOutline();
    updateStatus();
  }

  function save(saveAs = false) {
    if (!init.editable) return;
    if (!dirty && !saveAs && !init.isNew) return;
    doc = currentMarkdown();
    rebaseVisual(doc);
    post({ type: saveAs ? 'saveAs' : 'save', md: doc });
  }

  function setPreview(html) {
    C.innerHTML = html;
    enhance();
    refreshOutline();
    updateStatus();
  }

  // ---------- outline sidebar ----------
  const HEADINGS = 'h1,h2,h3,h4,h5,h6';
  let outline = [];   // { level, text, line? } in document order

  const stripInline = s => s
    .replace(/!?\[([^\]]*)\]\([^)]*\)/g, '$1')   // links, images
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

  function refreshOutline() {
    if ($('outline').hidden) return;
    outline = mode === 'preview'
      ? [...C.querySelectorAll(HEADINGS)].map(h => ({ level: +h.tagName[1], text: h.textContent.trim() }))
      : headingsFromText(currentMarkdown());
    const list = $('ol-list');
    if (!outline.length) { list.innerHTML = '<p class="ol-empty">No headings</p>'; return; }
    const min = Math.min(...outline.map(h => h.level));
    list.replaceChildren(...outline.map((h, i) => {
      const b = document.createElement('button');
      b.className = 'ol-item';
      b.style.paddingLeft = `${10 + (h.level - min) * 14}px`;
      b.textContent = h.text || '(empty heading)';
      b.title = h.text;
      b.addEventListener('click', () => goToHeading(i));
      return b;
    }));
    highlightOutline();
  }

  function goToHeading(i) {
    if (mode === 'preview') {
      C.querySelectorAll(HEADINGS)[i]?.scrollIntoView({ block: 'start' });
    } else if (editor?.isWysiwygMode()) {
      $('editor').querySelectorAll(`.toastui-editor-ww-container .ProseMirror :is(${HEADINGS})`)[i]?.scrollIntoView({ block: 'start' });
    } else if (editor && outline[i]?.line) {
      editor.setSelection([outline[i].line, 1], [outline[i].line, 1]);
      editor.focus();
      $('editor').querySelector('.toastui-editor-md-container .ProseMirror')?.querySelectorAll(':scope > div')[outline[i].line - 1]
        ?.scrollIntoView({ block: 'start' });
    }
  }

  // Mark the section currently at the top of the preview.
  function highlightOutline() {
    if ($('outline').hidden || mode !== 'preview') return;
    const top = $('preview').getBoundingClientRect().top + 80;
    let current = 0;
    C.querySelectorAll(HEADINGS).forEach((h, i) => { if (h.getBoundingClientRect().top <= top) current = i; });
    $('ol-list').querySelectorAll('.ol-item').forEach((b, i) => {
      b.classList.toggle('on', i === current);
      if (i === current) b.scrollIntoView({ block: 'nearest' });
    });
  }
  let scrollQueued = false;
  $('preview').addEventListener('scroll', () => {
    if (scrollQueued) return;
    scrollQueued = true;
    requestAnimationFrame(() => { scrollQueued = false; highlightOutline(); });
  });

  function toggleOutline(show = $('outline').hidden, remember = true) {
    $('outline').hidden = !show;
    $('outline-btn').classList.toggle('on', show);
    if (remember) try { localStorage.setItem('outline', show ? '1' : '0'); } catch {}
    refreshOutline();
  }
  // Other tabs follow when the outline is toggled in one of them.
  addEventListener('storage', e => { if (e.key === 'outline') toggleOutline(e.newValue === '1', false); });

  // ---------- status bar ----------
  function countWords(md) {
    const text = md
      .replace(/^(`{3,}|~{3,})[\s\S]*?^\1/gm, ' ')   // fenced code
      .replace(/!?\[([^\]]*)\]\([^)]*\)/g, '$1')
      .replace(/<[^>]+>/g, ' ');
    return (text.match(/[\p{L}\p{N}][\p{L}\p{N}'’_-]*/gu) || []).length;
  }

  function updateStatus() {
    const words = countWords(mode === 'preview' ? renderedMd : currentMarkdown());
    const minutes = Math.max(1, Math.round(words / 200));
    $('st-left').textContent = `${words.toLocaleString()} words · ${words ? minutes : 0} min read`;
    updateCaret();
  }

  function updateCaret() {
    const parts = [];
    if (editor && mode === 'markdown') {
      const [, [line, ch]] = editor.getSelection();
      parts.push(`Ln ${line}, Col ${ch}`);
    }
    parts.push(init.bom ? 'UTF-8 with BOM' : 'UTF-8', init.crlf ? 'CRLF' : 'LF');
    $('st-right').textContent = parts.join(' · ');
  }

  // Outline and word count follow edits, a moment after typing stops.
  let refreshTimer = 0;
  function scheduleRefresh() {
    clearTimeout(refreshTimer);
    refreshTimer = setTimeout(() => { refreshOutline(); updateStatus(); }, 300);
  }

  function apply(md, html) {
    doc = renderedMd = md;
    setPreview(html);
    if (editor) quiet(() => editor.setMarkdown(md, false));
    pending = null;
    rebaseVisual(md);
    $('banner').hidden = true;
    setDirty(false);
  }

  // ---------- called from C# ----------
  window.__update = setPreview;
  window.__getMarkdown = () => (dirty || init.isNew ? currentMarkdown() : doc);
  window.__saved = () => { init.isNew = false; setDirty(false); };
  window.__renamed = (path, baseHref) => {   // after "Save as"
    init.path = path;
    $('name').textContent = path;
    document.querySelector('base').href = location.origin + baseHref;
  };
  window.__external = (md, html) => {
    if (!dirty) return apply(md, html);
    pending = { md, html };
    $('banner').hidden = false;
  };

  // ---------- UI wiring ----------
  $('name').textContent = init.path || `${init.name} — not saved yet`;
  if (!init.editable) document.querySelectorAll('[data-mode]').forEach(b => { b.disabled = true; });
  document.querySelectorAll('[data-mode]').forEach(b => b.addEventListener('click', () => setMode(b.dataset.mode)));
  $('save').addEventListener('click', () => save());
  $('outline-btn').addEventListener('click', () => toggleOutline());
  $('b-load').addEventListener('click', () => pending && apply(pending.md, pending.html));
  $('b-keep').addEventListener('click', () => { pending = null; $('banner').hidden = true; });

  // Theme switched (from the app's theme button or Windows): CSS follows by itself;
  // the editor and Mermaid diagrams pick their theme in JS, so update those.
  darkQuery.addEventListener('change', e => {
    dark = e.matches;
    $('editor').querySelector('.toastui-editor-defaultUI')?.classList.toggle('toastui-editor-dark', dark);
    if (C.querySelector('.mermaid')) post({ type: 'render', md: renderedMd });
  });

  // In-page anchors (the <base> tag would otherwise turn them into navigations).
  C.addEventListener('click', e => {
    const a = e.target.closest('a[href^="#"]');
    if (!a) return;
    e.preventDefault();
    const t = document.getElementById(decodeURIComponent(a.getAttribute('href').slice(1)));
    if (t) t.scrollIntoView();
  });

  // Files dropped onto a document open as tabs (rather than into the editor).
  addEventListener('dragover', e => {
    if (e.dataTransfer.types.includes('Files')) { e.preventDefault(); e.stopPropagation(); }
  }, true);
  addEventListener('drop', e => {
    if (!e.dataTransfer.files.length) return;
    e.preventDefault(); e.stopPropagation();
    parent.shell.dropFiles(e.dataTransfer.files);
  }, true);

  addEventListener('keydown', e => {
    if (parent.shell.key(e)) return; // tab switching, open, close tab
    const k = e.key.toLowerCase(), ctrl = e.ctrlKey && !e.altKey;
    let handled = true;
    if (ctrl && k === 's') save(e.shiftKey);
    else if (ctrl && e.shiftKey && k === 'o') toggleOutline();
    else if (ctrl && k === '1') setMode('preview');
    else if (ctrl && k === '2') setMode('markdown');
    else if (ctrl && k === '3') setMode('visual');
    else if (ctrl && k === 'e') setMode(mode === 'preview' ? lastEditMode : 'preview');
    else if (k === 'f5' || (ctrl && k === 'r')) { if (init.path) post({ type: 'reload' }); }
    else handled = false;
    if (handled) { e.preventDefault(); e.stopPropagation(); }
  }, true);

  enhance();
  let outlineOpen = false;
  try { outlineOpen = localStorage.getItem('outline') === '1'; } catch {}
  if (outlineOpen) toggleOutline(true);
  updateStatus();
  if (init.isNew) setMode('markdown'); // a new file starts in the editor
})();
