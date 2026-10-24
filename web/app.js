(() => {
  const $ = id => document.getElementById(id);
  const init = JSON.parse($('init').textContent);
  const C = $('content');
  const darkQuery = matchMedia('(prefers-color-scheme: dark)');
  let dark = darkQuery.matches;
  // This page runs in a tab iframe; the shell (parent) relays messages to the app.
  const post = msg => parent.shell.fromDoc(init.id, msg);
  const { headingsFromText, countWords, protectMath, fixVisual, findAll, expandReplacement } = window.MdText; // web/text.js

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

  // Diagrams take their colors from the current theme's CSS variables.
  function mermaidTheme() {
    const cs = getComputedStyle(document.documentElement);
    const v = name => cs.getPropertyValue(name).trim();
    const hex = /^#[0-9a-f]{3,8}$/i;
    if (![v('--bg'), v('--fg'), v('--code-bg')].every(c => hex.test(c))) return { theme: dark ? 'dark' : 'default' };
    return {
      theme: 'base',
      themeVariables: {
        darkMode: dark, background: v('--bg'), fontFamily: getComputedStyle(C).fontFamily,
        primaryColor: v('--code-bg'), primaryTextColor: v('--fg'), primaryBorderColor: v('--border'),
        secondaryColor: v('--bar'), tertiaryColor: v('--bg'), lineColor: v('--muted'), textColor: v('--fg'),
        noteBkgColor: v('--warn-bg'), noteTextColor: v('--warn-fg'),
      },
    };
  }

  // An image that can't be loaded gets a visible note instead of an empty gap. Images that
  // failed before this script ran are caught by the scan in enhance().
  function markBroken(img) {
    if (img.dataset.broken) return;
    img.dataset.broken = '1';
    const src = img.getAttribute('src') ?? '';
    const note = document.createElement('span');
    note.className = 'img-missing';
    let shown = src;
    try { shown = decodeURI(src); } catch {}
    note.textContent = /^https?:/i.test(src) ? `Image couldn't be loaded: ${src}` : `Image not found: ${shown}`;
    note.title = img.src;
    img.after(note);
  }
  C.addEventListener('error', e => { if (e.target.tagName === 'IMG') markBroken(e.target); }, true);

  async function enhance() {
    C.querySelectorAll('img').forEach(i => { if (i.complete && !i.naturalWidth && i.getAttribute('src')) markBroken(i); });
    const codes = C.querySelectorAll('pre > code');
    if (codes.length) try {
      await load('hljs/highlight.min.js');
      codes.forEach(c => { if (!c.closest('.mermaid')) hljs.highlightElement(c); });
    } catch {}
    const diagrams = C.querySelectorAll('.mermaid');
    if (diagrams.length) try {
      await load('mermaid/mermaid.min.js');
      mermaid.initialize({ startOnLoad: false, ...mermaidTheme() });
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
        initialValue: type === 'wysiwyg' ? protectMath(doc) : doc, usageStatistics: false, hideModeSwitch: true, theme: dark ? 'dark' : 'light',
        frontMatter: true, // keep a leading YAML block as is (Visual mode would otherwise rewrite it)
        hooks: { addImageBlobHook: addImage },
      });
    });
    editor.on('change', () => {
      scheduleRefresh();
      if (!$('find').hidden) scheduleFind();
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

    closeFind(false);
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
        if (toVisual) editor.setMarkdown(protectMath(source), false); // math survives Visual mode as code
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
    if (editor) quiet(() => editor.setMarkdown(editor.isWysiwygMode() ? protectMath(md) : md, false));
    pending = null;
    rebaseVisual(md);
    $('banner').hidden = true;
    setDirty(false);
  }

  // ---------- find & replace (Markdown and Visual modes) ----------
  // Searches the editor's ProseMirror document directly, so matches map to exact positions.
  // All matches are highlighted with the CSS Custom Highlight API; the current one is
  // selected in the editor when the bar closes. Preview mode keeps the built-in Ctrl+F.
  const findBar = document.createElement('div');
  findBar.id = 'find';
  findBar.hidden = true;
  findBar.innerHTML = `
    <div class="f-row">
      <button id="f-more" class="f-btn" title="Toggle replace (Ctrl+H)">›</button>
      <input id="f-query" placeholder="Find" spellcheck="false" autocomplete="off">
      <button id="f-case" class="f-btn f-opt" title="Match case">Aa</button>
      <button id="f-word" class="f-btn f-opt" title="Match whole word"><u>ab</u></button>
      <button id="f-regex" class="f-btn f-opt" title="Use regular expression">.*</button>
      <span id="f-count"></span>
      <button id="f-prev" class="f-btn" title="Previous match (Shift+Enter)">↑</button>
      <button id="f-next" class="f-btn" title="Next match (Enter)">↓</button>
      <button id="f-close" class="f-btn" title="Close (Esc)">×</button>
    </div>
    <div class="f-row" id="f-replace-row" hidden>
      <span class="f-spacer"></span>
      <input id="f-replace" placeholder="Replace" spellcheck="false" autocomplete="off">
      <button id="f-one" class="f-btn f-text" title="Replace (Enter)">Replace</button>
      <button id="f-all" class="f-btn f-text" title="Replace all (Ctrl+Alt+Enter)">All</button>
    </div>`;
  $('main').append(findBar);

  const findOpts = { matchCase: false, wholeWord: false, regex: false };
  let found = [], current = -1, findTimer = 0, foundText = '';
  // 1-based [line, column] of an offset in the searched text (Markdown mode positions).
  const lineCh = offset => {
    const before = foundText.slice(0, offset);
    return [before.split('\n').length, offset - before.lastIndexOf('\n')];
  };
  const pmView = () => editor?.getCurrentModeEditor()?.view;
  const highlights = typeof Highlight === 'function' && CSS.highlights;

  // The editor document as one string, with the ProseMirror position of every character.
  // Text blocks are joined with "\n"; inline non-text nodes (images, breaks) become U+FFFC.
  function docText(view) {
    let text = '';
    const pos = [];
    view.state.doc.descendants((node, at) => {
      if (!node.isTextblock) return true;
      if (pos.length) { text += '\n'; pos.push(-1); }
      node.forEach((child, offset) => {
        const start = at + 1 + offset;
        if (child.isText) { text += child.text; for (let i = 0; i < child.text.length; i++) pos.push(start + i); }
        else { text += '￼'; pos.push(start); }
      });
      return false;
    });
    return { text, pos };
  }

  function runFind(keepPlace) {
    const view = pmView();
    const query = $('f-query').value;
    const previousFrom = found[current]?.from;
    found = [];
    if (view && query) {
      const { text, pos } = docText(view);
      foundText = text;
      const matches = findAll(text, query, findOpts, 5000);
      $('f-query').classList.toggle('bad', matches === null);
      for (const m of matches ?? []) {
        if (/[\n￼]/.test(text.slice(m.start, m.end))) continue; // across blocks or over an image
        found.push({ from: pos[m.start], to: pos[m.end - 1] + 1, match: m });
      }
    } else $('f-query').classList.remove('bad');
    // Stay on (or just after) the match we were on; otherwise start at the cursor.
    const anchor = keepPlace && previousFrom != null ? previousFrom : view?.state.selection.from ?? 0;
    current = found.length ? Math.max(0, found.findIndex(f => f.from >= anchor)) : -1;
    showFind(false);
  }
  function scheduleFind() { clearTimeout(findTimer); findTimer = setTimeout(() => runFind(true), 150); }

  function rangeFor(view, f) {
    try {
      const a = view.domAtPos(f.from), b = view.domAtPos(f.to);
      const r = new Range();
      r.setStart(a.node, a.offset);
      r.setEnd(b.node, b.offset);
      return r;
    } catch { return null; }
  }

  function showFind(scroll) {
    const view = pmView();
    const q = $('f-query').value;
    $('f-count').textContent = !q ? '' : $('f-query').classList.contains('bad') ? 'Invalid pattern'
      : found.length ? `${current + 1} of ${found.length}${found.length >= 5000 ? '+' : ''}` : 'No results';
    for (const id of ['f-prev', 'f-next', 'f-one', 'f-all']) $(id).disabled = !found.length;
    if (!highlights || !view) return;
    const all = found.slice(0, 2000).map(f => rangeFor(view, f)).filter(Boolean);
    CSS.highlights.set('find', new Highlight(...all));
    const cur = found[current] && rangeFor(view, found[current]);
    if (cur) {
      CSS.highlights.set('find-current', new Highlight(cur));
      if (scroll) {
        const el = cur.startContainer.nodeType === 3 ? cur.startContainer.parentElement : cur.startContainer;
        const box = cur.getBoundingClientRect(), area = view.dom.parentElement.getBoundingClientRect();
        if (box.top < area.top + 60 || box.bottom > area.bottom - 20) el.scrollIntoView({ block: 'center' });
      }
    } else CSS.highlights.delete('find-current');
  }

  function step(dir) {
    if (!found.length) return;
    current = (current + dir + found.length) % found.length;
    showFind(true);
  }

  function replaceOne() {
    const view = pmView(), f = found[current];
    if (!view || !f) return;
    const text = expandReplacement(f.match, $('f-replace').value, findOpts.regex);
    view.dispatch(view.state.tr.insertText(text, f.from, f.to));
    runFind(false);      // positions changed; continue from the replaced spot
    current = found.length ? Math.max(0, found.findIndex(m => m.from >= f.from + text.length)) : -1;
    showFind(true);
  }

  function replaceAll() {
    const view = pmView();
    if (!view || !found.length) return;
    const count = found.length;
    const tr = view.state.tr;   // one transaction: a single Ctrl+Z undoes it
    for (const f of [...found].reverse()) tr.insertText(expandReplacement(f.match, $('f-replace').value, findOpts.regex), f.from, f.to);
    view.dispatch(tr);
    runFind(false);
    parent.shell.toast(`Replaced ${count} ${count === 1 ? 'match' : 'matches'}.`);
  }

  function openFind(withReplace) {
    if (!editor || mode === 'preview') return;
    const selected = editor.getSelectedText?.() ?? '';
    if (selected && !selected.includes('\n')) $('f-query').value = selected;
    if (withReplace !== undefined) $('f-replace-row').hidden = !withReplace;
    $('f-more').textContent = $('f-replace-row').hidden ? '›' : '⌄';
    findBar.hidden = false;
    placeFind();
    $(withReplace && $('f-query').value ? 'f-replace' : 'f-query').focus();
    $(withReplace && $('f-query').value ? 'f-replace' : 'f-query').select();
    runFind(false);
    showFind(true);
  }

  // Just below the editor toolbar, at the right of the text being searched
  // (in Markdown mode that's the source pane, not the preview beside it).
  function placeFind() {
    const main = $('main').getBoundingClientRect();
    const tb = $('editor').querySelector('.toastui-editor-toolbar');
    const pane = editor.isWysiwygMode() ? null : $('editor').querySelector('.toastui-editor-md-container .toastui-editor');
    findBar.style.top = (tb ? tb.getBoundingClientRect().bottom - main.top + 6 : 8) + 'px';
    const right = pane ? main.right - pane.getBoundingClientRect().right + 16 : 24;
    findBar.style.right = Math.max(8, Math.min(right, main.width - findBar.offsetWidth - 8)) + 'px';
  }
  addEventListener('resize', () => { if (!findBar.hidden) placeFind(); });

  // Closing selects the current match in the editor (so typing continues there).
  function closeFind(focusEditor = true) {
    if (findBar.hidden) return;
    findBar.hidden = true;
    clearTimeout(findTimer);
    if (highlights) { CSS.highlights.delete('find'); CSS.highlights.delete('find-current'); }
    const f = found[current];
    if (focusEditor && editor) {
      if (f && editor.isWysiwygMode()) editor.setSelection(f.from, f.to);
      else if (f) editor.setSelection(lineCh(f.match.start), lineCh(f.match.end)); // Markdown mode: [line, ch], one block per line
      editor.focus();
    }
    found = []; current = -1;
  }

  $('f-query').addEventListener('input', () => { runFind(false); showFind(true); });
  $('f-replace').addEventListener('keydown', e => {
    if (e.key === 'Enter' && e.ctrlKey && e.altKey) { e.preventDefault(); replaceAll(); }
    else if (e.key === 'Enter') { e.preventDefault(); replaceOne(); }
  });
  $('f-query').addEventListener('keydown', e => {
    if (e.key === 'Enter' && e.ctrlKey && e.altKey) { e.preventDefault(); replaceAll(); }
    else if (e.key === 'Enter') { e.preventDefault(); step(e.shiftKey ? -1 : 1); }
  });
  for (const [id, key] of [['f-case', 'matchCase'], ['f-word', 'wholeWord'], ['f-regex', 'regex']]) {
    $(id).addEventListener('click', () => {
      findOpts[key] = !findOpts[key];
      $(id).classList.toggle('on', findOpts[key]);
      runFind(true);
      showFind(true);
      $('f-query').focus();
    });
  }
  $('f-more').addEventListener('click', () => openFind($('f-replace-row').hidden));
  $('f-prev').addEventListener('click', () => step(-1));
  $('f-next').addEventListener('click', () => step(1));
  $('f-one').addEventListener('click', replaceOne);
  $('f-all').addEventListener('click', replaceAll);
  $('f-close').addEventListener('click', () => closeFind());
  // Clicking into the editor ends the search where the user clicked.
  $('editor').addEventListener('mousedown', () => { if (!findBar.hidden) closeFind(false); });

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

  // Theme changed (Appearance panel or Windows): CSS follows by itself; the editor and Mermaid
  // diagrams pick their colors in JS, so update those. Both triggers can fire for one change.
  let themeTimer = 0;
  function themeChanged() {
    clearTimeout(themeTimer);
    themeTimer = setTimeout(() => {
      dark = darkQuery.matches;
      $('editor').querySelector('.toastui-editor-defaultUI')?.classList.toggle('toastui-editor-dark', dark);
      if (C.querySelector('.mermaid')) post({ type: 'render', md: renderedMd });
    }, 50);
  }
  darkQuery.addEventListener('change', themeChanged);

  // Called by the shell with the new /__theme.css version (same loader as the shell's).
  window.__setTheme = version => parent.reloadThemeCss(document, version, themeChanged);
  // In-page anchors (the <base> tag would otherwise turn them into navigations).
  C.addEventListener('click', e => {
    const a = e.target.closest('a[href^="#"]');
    if (!a) return;
    e.preventDefault();
    const t = document.getElementById(decodeURIComponent(a.getAttribute('href').slice(1)));
    if (t) t.scrollIntoView();
  });

  // Other links (web pages, other files) go to the app, which opens them in a tab or the default
  // program. Letting them navigate would replace this document with WebView2's "blocked" page.
  // Links inside the Visual editor stay editable text.
  function openLink(e) {
    const a = e.target.closest('a[href]');
    if (!a || e.defaultPrevented || a.getAttribute('href').startsWith('#') || a.closest('[contenteditable="true"]')) return;
    if (e.button !== 0 && e.button !== 1) return;
    e.preventDefault();
    post({ type: 'link', href: a.href });
  }
  addEventListener('click', openLink);
  addEventListener('auxclick', openLink); // middle click

  // Files dropped onto a document open as tabs, except images dropped into an editor,
  // which the editor inserts (see addImage).
  const imagesIntoEditor = e => mode !== 'preview' && editor && e.dataTransfer.items.length > 0 &&
    [...e.dataTransfer.items].every(i => i.kind === 'file' && i.type.startsWith('image/'));
  addEventListener('dragover', e => {
    if (e.dataTransfer.types.includes('Files') && !imagesIntoEditor(e)) { e.preventDefault(); e.stopPropagation(); }
  }, true);
  addEventListener('drop', e => {
    if (!e.dataTransfer.files.length || imagesIntoEditor(e)) return;
    e.preventDefault(); e.stopPropagation();
    parent.shell.dropFiles(e.dataTransfer.files);
  }, true);

  // ---------- pasted and dropped images ----------
  // The app saves them in an "images" folder next to the document; the editor then
  // inserts ![name](images/…). An unsaved new file has no folder yet.
  const imageRequests = new Map();
  let imageRequest = 0;
  async function addImage(blob, insert) {
    if (!init.path) { parent.shell.toast('Save the file first: pasted images are stored in a folder next to it.'); return; }
    const data = await new Promise((ok, fail) => {
      const r = new FileReader();
      r.onload = () => ok(r.result.slice(r.result.indexOf(',') + 1)); // base64 part of the data: URL
      r.onerror = fail;
      r.readAsDataURL(blob);
    });
    const req = ++imageRequest;
    imageRequests.set(req, insert);
    post({ type: 'saveImage', req, name: blob.name ?? '', mime: blob.type, data });
  }
  window.__imageSaved = (req, link) => {
    const insert = imageRequests.get(req);
    imageRequests.delete(req);
    if (insert && link) insert(link, link.split('/').pop().replace(/\.[^.]+$/, ''));
  };

  addEventListener('keydown', e => {
    if (parent.shell.key(e)) return; // tab switching, open, close tab
    if (docKey(e)) { e.preventDefault(); e.stopPropagation(); }
  }, true);
  // The shell passes shortcuts here that were pressed while the focus was in the tab bar.
  window.__key = docKey;

  // Document shortcuts; returns whether the key was one of them.
  function docKey(e) {
    const k = e.key.toLowerCase(), ctrl = e.ctrlKey && !e.altKey;
    let handled = true;
    if (k === 'escape' && !findBar.hidden) closeFind();
    else if (k === 'escape' && parent.shell.isFullScreen()) parent.shell.exitFullScreen();
    else if (ctrl && !e.shiftKey && k === 'f' && mode !== 'preview') openFind();
    else if (ctrl && !e.shiftKey && k === 'h' && init.editable) {
      (mode === 'preview' ? setMode(lastEditMode) : Promise.resolve()).then(() => openFind(true));
    }
    else if (k === 'f3' && !findBar.hidden) step(e.shiftKey ? -1 : 1);
    else if (ctrl && k === 's') save(e.shiftKey);
    else if (ctrl && e.shiftKey && k === 'o') toggleOutline();
    else if (ctrl && k === '1') setMode('preview');
    else if (ctrl && k === '2') setMode('markdown');
    else if (ctrl && k === '3') setMode('visual');
    else if (ctrl && k === 'e') setMode(mode === 'preview' ? lastEditMode : 'preview');
    else if (k === 'f5' || (ctrl && k === 'r')) { if (init.path) post({ type: 'reload' }); }
    else handled = false;
    return handled;
  }

  // Full screen: the shell toggles body.focus on open documents; a tab opened later checks here.
  // The mouse at the top edge shows the shell's exit button (the document covers that area).
  if (parent.shell.isFullScreen()) document.body.classList.add('focus');
  addEventListener('mousemove', e => { if (document.body.classList.contains('focus')) parent.shell.peek(e.clientY); });

  enhance();
  let outlineOpen = false;
  try { outlineOpen = localStorage.getItem('outline') === '1'; } catch {}
  if (outlineOpen) toggleOutline(true);
  updateStatus();
  if (init.isNew) setMode('markdown'); // a new file starts in the editor
})();
