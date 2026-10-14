// The window: a tab bar plus one iframe per open document (web/app.js runs inside each).
(() => {
  const $ = id => document.getElementById(id);
  const tablist = $('tablist'), frames = $('frames');
  const tabs = new Map();   // id -> { tab, frame }
  let active = null;

  const post = (msg, files) => files
    ? chrome.webview.postMessageWithAdditionalObjects(msg, files)
    : chrome.webview.postMessage(msg);

  const order = () => [...tablist.children].map(t => +t.dataset.id);
  const sendOrder = () => post({ type: 'order', ids: order() });

  function addTab(id, name, path, activate) {
    if (tabs.has(id)) return activate && activateTab(id);
    const tab = document.createElement('div');
    tab.className = 'tab';
    tab.draggable = true;
    tab.dataset.id = id;
    tab.setAttribute('role', 'tab');
    tab.innerHTML = '<span class="label"></span><button class="x" title="Close (Ctrl+W)" tabindex="-1">×</button>';
    setTabName(tab, name, path);
    tab.addEventListener('mousedown', e => { if (e.button === 1) e.preventDefault(); });
    tab.addEventListener('auxclick', e => { if (e.button === 1) requestClose(id); });
    tab.addEventListener('click', e => e.target.closest('.x') ? requestClose(id) : activateTab(id));
    tab.addEventListener('contextmenu', e => { e.preventDefault(); activateTab(id); tabMenu(id, e.clientX, e.clientY); });

    // drag to reorder
    tab.addEventListener('dragstart', e => { e.dataTransfer.setData('application/x-tab', String(id)); e.dataTransfer.effectAllowed = 'move'; tab.classList.add('dragging'); });
    tab.addEventListener('dragend', () => { tab.classList.remove('dragging'); sendOrder(); });

    const frame = document.createElement('iframe');
    frame.src = `/__doc__.html?id=${id}`;
    frame.dataset.id = id;

    tablist.append(tab);
    frames.append(frame);
    tabs.set(id, { tab, frame });
    update();
    sendOrder();
    if (activate || active === null) activateTab(id);
  }

  function activateTab(id) {
    const t = tabs.get(id);
    if (!t) return;
    active = id;
    for (const [k, v] of tabs) {
      v.tab.classList.toggle('on', k === id);
      v.tab.setAttribute('aria-selected', k === id);
      v.frame.classList.toggle('on', k === id);
    }
    t.tab.scrollIntoView({ block: 'nearest', inline: 'nearest' });
    t.frame.focus();
    post({ type: 'active', doc: id });
  }

  function removeTab(id) {
    const t = tabs.get(id);
    if (!t) return;
    const ids = order(), i = ids.indexOf(id);
    t.tab.remove();
    t.frame.remove();
    tabs.delete(id);
    if (active === id) {
      active = null;
      const next = ids[i + 1] ?? ids[i - 1];
      if (next !== undefined) activateTab(next);
      else post({ type: 'active', doc: null });
    }
    update();
    sendOrder();
  }

  function cycle(step) {
    const ids = order();
    if (ids.length < 2) return;
    activateTab(ids[(ids.indexOf(active) + step + ids.length) % ids.length]);
  }

  function setTabName(tab, name, path) {
    tab.querySelector('.label').textContent = name;
    tab.dataset.path = path;
    tab.title = path || `${name} (not saved yet)`;
  }

  const requestClose = id => post({ type: 'closeTab', doc: id });
  const exportDoc = format => { if (active !== null) post({ type: 'export', format, doc: active }); };

  // ---------- popup menus (tab right-click, open/recent) ----------
  let menu = null;
  const closeMenu = () => { menu?.remove(); menu = null; };

  // items: { label, sub?, key?, title?, disabled?, action } or '-' for a separator
  function showMenu(items, x, y) {
    closeMenu();
    menu = document.createElement('div');
    menu.className = 'menu';
    menu.setAttribute('role', 'menu');
    for (const it of items) {
      if (it === '-') { menu.append(Object.assign(document.createElement('div'), { className: 'sep' })); continue; }
      const b = document.createElement('button');
      b.className = 'item';
      b.disabled = !!it.disabled;
      b.title = it.title ?? '';
      b.innerHTML = '<span class="l"><span class="t"></span><span class="s"></span></span><span class="k"></span>';
      b.querySelector('.t').textContent = it.label;
      b.querySelector('.s').textContent = it.sub ?? '';
      b.querySelector('.k').textContent = it.key ?? '';
      b.addEventListener('click', () => { closeMenu(); it.action(); });
      menu.append(b);
    }
    document.body.append(menu);
    const r = menu.getBoundingClientRect();
    menu.style.left = Math.max(4, Math.min(x, innerWidth - r.width - 4)) + 'px';
    menu.style.top = Math.max(4, Math.min(y, innerHeight - r.height - 4)) + 'px';
    menu.querySelector('.item:not(:disabled)')?.focus();
  }
  addEventListener('mousedown', e => {
    if (menu && !menu.contains(e.target) && !e.target.closest('#open-menu')) closeMenu();
  }, true);
  addEventListener('blur', closeMenu); // focus went into a document iframe
  addEventListener('resize', closeMenu);

  function tabMenu(id, x, y) {
    const ids = order(), i = ids.indexOf(id);
    const path = tabs.get(id)?.tab.dataset.path;
    showMenu([
      { label: 'Close', key: 'Ctrl+W', action: () => requestClose(id) },
      { label: 'Close others', disabled: ids.length < 2, action: () => post({ type: 'closeTabs', ids: ids.filter(x => x !== id) }) },
      { label: 'Close tabs to the right', disabled: i === ids.length - 1, action: () => post({ type: 'closeTabs', ids: ids.slice(i + 1) }) },
      '-',
      { label: 'Copy path', disabled: !path, action: () => post({ type: 'copyPath', doc: id }) },
      { label: 'Show in Explorer', disabled: !path, action: () => post({ type: 'reveal', doc: id }) },
      '-',
      { label: 'Reopen closed tab', key: 'Ctrl+Shift+T', action: () => post({ type: 'reopenClosed' }) },
    ], x, y);
  }

  // ---------- recent files ----------
  let recent = [];
  const fileName = p => p.split(/[\\/]/).pop();
  const dirName = p => p.slice(0, p.length - fileName(p).length - 1);

  function setRecent(list) {
    recent = list;
    const box = $('recent'), ul = box.querySelector('ul');
    ul.replaceChildren(...list.slice(0, 8).map(p => {
      const li = document.createElement('li');
      const b = document.createElement('button');
      b.title = p;
      b.innerHTML = '<span class="t"></span><span class="s"></span>';
      b.querySelector('.t').textContent = fileName(p);
      b.querySelector('.s').textContent = dirName(p);
      b.addEventListener('click', () => post({ type: 'openPath', path: p }));
      li.append(b);
      return li;
    }));
    box.hidden = list.length === 0;
  }

  function openMenu() {
    const r = $('open-menu').getBoundingClientRect();
    showMenu([
      { label: 'New file', key: 'Ctrl+N', action: () => post({ type: 'new' }) },
      { label: 'Open file…', key: 'Ctrl+O', action: () => post({ type: 'open' }) },
      '-',
      { label: 'Export as PDF…', disabled: active === null, action: () => exportDoc('pdf') },
      { label: 'Export as HTML…', disabled: active === null, action: () => exportDoc('html') },
      { label: 'Print…', key: 'Ctrl+P', disabled: active === null, action: () => exportDoc('print') },
      ...(recent.length ? ['-'] : []),
      ...recent.map(p => ({ label: fileName(p), sub: dirName(p), title: p, action: () => post({ type: 'openPath', path: p }) })),
      ...(recent.length ? ['-', { label: 'Clear recent files', action: () => post({ type: 'clearRecent' }) }] : []),
      '-',
      { label: 'Check for updates', sub: `Version ${document.body.dataset.version}`, action: () => post({ type: 'checkUpdates' }) },
    ], r.left, r.bottom + 2);
  }

  // ---------- update banner & toast ----------
  function showUpdate({ version, canInstall }) {
    $('update-text').textContent = `Markdown Viewer ${version} is available.`;
    $('update-install').textContent = canInstall ? 'Install update' : 'Download';
    $('update-install').title = canInstall ? 'Download and install, then restart the app' : 'Open the download page';
    updateStatus('', false);
    $('update').hidden = false;
  }

  function updateStatus(text, busy) {
    if (text) $('update-text').textContent = text;
    for (const id of ['update-install', 'update-notes', 'update-skip', 'update-close']) $(id).disabled = busy;
  }

  let toastTimer = 0;
  function toast(text) {
    $('toast').textContent = text;
    $('toast').hidden = false;
    clearTimeout(toastTimer);
    toastTimer = setTimeout(() => { $('toast').hidden = true; }, 4000);
  }

  $('update-install').addEventListener('click', () => post({ type: 'updateInstall' }));
  $('update-notes').addEventListener('click', () => post({ type: 'updateNotes' }));
  $('update-skip').addEventListener('click', () => { post({ type: 'updateSkip' }); $('update').hidden = true; });
  $('update-close').addEventListener('click', () => { $('update').hidden = true; });
  const update = () => { $('empty').hidden = tabs.size > 0; document.body.classList.toggle('has-tabs', tabs.size > 0); };

  // Shared shortcuts; documents forward theirs here too.
  function handleKey(e, fromDoc) {
    const k = e.key.toLowerCase(), ctrl = e.ctrlKey && !e.altKey;
    if (k === 'escape' && menu) closeMenu();
    else if (k === 'escape' && !panel.hidden) toggleSettings(false);
    else if (ctrl && k === 'tab') cycle(e.shiftKey ? -1 : 1);
    else if (ctrl && e.shiftKey && k === 't') post({ type: 'reopenClosed' });
    else if (ctrl && !e.shiftKey && k === 'n') post({ type: 'new' });
    else if (ctrl && !e.shiftKey && k === 'p') exportDoc('print');
    else if (ctrl && k === 'pagedown') cycle(1);
    else if (ctrl && k === 'pageup') cycle(-1);
    else if (ctrl && !e.shiftKey && k === 'o') post({ type: 'open' });
    else if (ctrl && k === 'w') active !== null ? requestClose(active) : post({ type: 'closeWindow' });
    else if (!fromDoc && ctrl && (k === 'f4')) active !== null && requestClose(active);
    else return false;
    e.preventDefault();
    e.stopPropagation();
    return true;
  }
  addEventListener('keydown', e => handleKey(e, false), true);

  // Reorder by dragging tabs.
  tablist.addEventListener('dragover', e => {
    if (!e.dataTransfer.types.includes('application/x-tab')) return;
    e.preventDefault();
    const dragging = tablist.querySelector('.dragging');
    const over = e.target.closest('.tab');
    if (!dragging || !over || over === dragging) return;
    const r = over.getBoundingClientRect();
    tablist.insertBefore(dragging, e.clientX < r.left + r.width / 2 ? over : over.nextSibling);
  });

  // Mouse wheel scrolls an overflowing tab strip horizontally.
  tablist.addEventListener('wheel', e => { if (e.deltaY) { tablist.scrollLeft += e.deltaY; e.preventDefault(); } }, { passive: false });

  // Files dropped anywhere open as tabs.
  addEventListener('dragover', e => { if (e.dataTransfer.types.includes('Files')) e.preventDefault(); });
  addEventListener('drop', e => { if (e.dataTransfer.files.length) { e.preventDefault(); post({ type: 'drop' }, e.dataTransfer.files); } });

  // ---------- appearance: theme, text and background ----------
  const panel = $('settings');
  let look = null;          // last state from the app
  let themesKey = '';       // to rebuild the theme cards only when the list changes

  // Loads /__theme.css?v=<version> and, once it's in, removes all older copies, so quick
  // successive changes can't leave a stale sheet behind (the newest is always last).
  function reloadThemeCss(doc, version, done) {
    const sheets = () => [...doc.querySelectorAll('link[data-theme-css]')];
    if (sheets().some(l => +l.dataset.v >= version)) return;
    const link = doc.createElement('link');
    link.rel = 'stylesheet';
    link.href = `/__theme.css?v=${version}`;
    link.dataset.themeCss = '';
    link.dataset.v = version;
    link.onload = link.onerror = () => { sheets().forEach(l => { if (+l.dataset.v < version) l.remove(); }); done?.(); };
    doc.head.append(link);
  }
  window.reloadThemeCss = reloadThemeCss;
  const seg = (key, options) =>
    `<div class="seg" data-key="${key}">${options.map(([v, label]) => `<button data-v="${v}">${label}</button>`).join('')}</div>`;

  function buildPanel() {
    panel.innerHTML = `
      <header><h2>Appearance</h2><button class="x" id="set-close" title="Close (Esc)">\u00d7</button></header>
      <section>
        <h3>Theme</h3>
        <div id="theme-grid" role="radiogroup" aria-label="Theme"></div>
        <div class="buttons">
          <button id="theme-create" title="Copy the current theme into a file you can edit">New theme from current</button>
          <button id="theme-folder">Open themes folder</button>
        </div>
      </section>
      <section>
        <h3>Text</h3>
        <div class="row"><span>Font</span>${seg('font', [['sans', 'Sans'], ['serif', 'Serif']])}</div>
        <div class="row"><span>Size</span><input type="range" id="fontSize" min="12" max="26" step="1"><output id="fontSize-out"></output></div>
        <div class="row"><span>Width</span>${seg('width', [['narrow', 'Narrow'], ['normal', 'Normal'], ['wide', 'Wide'], ['full', 'Full']])}</div>
      </section>
      <section>
        <h3>Background</h3>
        <div id="bg-thumb"></div>
        <div class="buttons">
          <button id="bg-choose">Choose image\u2026</button>
          <button id="bg-remove">Remove</button>
        </div>
        <div id="bg-options">
          <div class="row"><span>Fit</span>${seg('backgroundFit', [['cover', 'Fill'], ['contain', 'Fit'], ['tile', 'Tile']])}</div>
          <div class="row"><span>Visibility</span><input type="range" id="backgroundOpacity" min="5" max="100" step="5"><output id="backgroundOpacity-out"></output></div>
          <div class="row"><span>Blur</span><input type="range" id="backgroundBlur" min="0" max="30" step="1"><output id="backgroundBlur-out"></output></div>
          <label class="row check"><input type="checkbox" id="paper"><span>Paper behind text</span></label>
        </div>
      </section>
      <footer><button id="appearance-reset">Reset text and background</button></footer>`;

    $('set-close').addEventListener('click', () => toggleSettings(false));
    $('theme-create').addEventListener('click', () => post({ type: 'themeCreate' }));
    $('theme-folder').addEventListener('click', () => post({ type: 'themesFolder' }));
    $('bg-choose').addEventListener('click', () => post({ type: 'bgChoose' }));
    $('bg-remove').addEventListener('click', () => post({ type: 'bgRemove' }));
    $('appearance-reset').addEventListener('click', () => post({ type: 'appearanceReset' }));
    panel.querySelectorAll('.seg').forEach(s => s.addEventListener('click', e => {
      const b = e.target.closest('button');
      if (b) post({ type: 'appearance', [s.dataset.key]: b.dataset.v });
    }));
    // Sliders: show the value while dragging, apply at most every 120 ms.
    for (const id of ['fontSize', 'backgroundOpacity', 'backgroundBlur']) {
      let timer = 0;
      $(id).addEventListener('input', () => {
        showSliderValue(id);
        clearTimeout(timer);
        timer = setTimeout(() => post({ type: 'appearance', [id]: +$(id).value }), 120);
      });
    }
    $('paper').addEventListener('change', () => post({ type: 'appearance', paper: $('paper').checked }));
  }

  const units = { fontSize: ' px', backgroundOpacity: '%', backgroundBlur: ' px' };
  const showSliderValue = id => { $(id + '-out').textContent = $(id).value + units[id]; };

  function renderThemes(themes) {
    const key = JSON.stringify(themes);
    if (key === themesKey) return;
    themesKey = key;
    $('theme-grid').replaceChildren(...themes.map(t => {
      const card = document.createElement('button');
      card.className = 'theme-card';
      card.dataset.id = t.id;
      card.setAttribute('role', 'radio');
      card.title = t.id === 'system' ? 'Light or dark, following Windows' : `${t.name} (${t.dark ? 'dark' : 'light'})`;
      const s = t.swatch;
      card.innerHTML = `
        <span class="tc-preview" style="background:${s.bg};border-color:${s.border}">
          <span class="tc-bar" style="background:${s.bar}"></span>
          <span class="tc-text" style="color:${s.fg}">Aa</span>
          <span class="tc-dot" style="background:${s.accent}"></span>
        </span>
        <span class="tc-name"></span>`;
      card.querySelector('.tc-name').textContent = t.name;
      if (t.custom) {
        const edit = document.createElement('span');
        edit.className = 'tc-edit';
        edit.title = 'Edit this theme';
        edit.textContent = 'Edit';
        edit.addEventListener('click', e => { e.stopPropagation(); post({ type: 'themeEdit', id: t.id }); });
        card.append(edit);
      }
      card.addEventListener('click', () => post({ type: 'appearance', theme: t.id }));
      return card;
    }));
  }

  function applyAppearance(state) {
    look = state;
    reloadThemeCss(document, state.version);
    for (const { frame } of tabs.values()) frame.contentWindow.__setTheme?.(state.version);

    if (!panel.firstChild) buildPanel();
    renderThemes(state.themes);
    panel.querySelectorAll('.theme-card').forEach(c => {
      c.classList.toggle('on', c.dataset.id === state.theme);
      c.setAttribute('aria-checked', c.dataset.id === state.theme);
    });
    panel.querySelectorAll('.seg').forEach(s => s.querySelectorAll('button').forEach(b =>
      b.classList.toggle('on', b.dataset.v === String(state[s.dataset.key]))));
    for (const id of ['fontSize', 'backgroundOpacity', 'backgroundBlur']) {
      if (document.activeElement !== $(id)) $(id).value = state[id]; // don't fight a slider being dragged
      showSliderValue(id);
    }
    $('paper').checked = state.paper;
    const bg = state.background;
    $('bg-thumb').innerHTML = bg ? '' : '<span>No background</span>';
    $('bg-thumb').style.backgroundImage = bg ? `url("${bg.url}")` : '';
    $('bg-thumb').title = bg ? bg.name : '';
    $('bg-remove').disabled = !bg;
    $('bg-options').classList.toggle('disabled', !bg);
    $('bg-options').querySelectorAll('input, button').forEach(el => { el.disabled = !bg; });
  }

  function toggleSettings(show = panel.hidden) {
    panel.hidden = !show;
    $('appearance-btn').classList.toggle('on', show);
  }
  $('appearance-btn').addEventListener('click', () => toggleSettings());
  $('new').addEventListener('click', () => post({ type: 'new' }));
  $('open-menu').addEventListener('click', () => menu ? closeMenu() : openMenu());
  $('new-empty').addEventListener('click', () => post({ type: 'new' }));
  $('open-empty').addEventListener('click', () => post({ type: 'open' }));
  // Double-click on empty tab-strip space: new file, like in a browser.
  $('tabs').addEventListener('dblclick', e => {
    if (e.target === tablist || e.target === $('tabs') || e.target.classList.contains('spacer')) post({ type: 'new' });
  });
  $('tabs').addEventListener('contextmenu', e => { if (!e.target.closest('.tab')) e.preventDefault(); });

  window.shell = {
    addTab,
    removeTab,
    activate: activateTab,
    renameTab(id, name, path) { const t = tabs.get(id); if (t) setTabName(t.tab, name, path); },
    setRecent,
    showUpdate,
    applyAppearance,
    themeVersion: () => look?.version,
    updateStatus,
    toast,
    reload: id => tabs.get(id)?.frame.contentWindow.location.reload(),
    call(id, fn, ...args) {
      const w = tabs.get(id)?.frame.contentWindow;
      return w && typeof w[fn] === 'function' ? w[fn](...args) : null;
    },
    // Messages from a document iframe.
    fromDoc(id, msg) {
      if (msg.type === 'dirty') tabs.get(id)?.tab.classList.toggle('dirty', msg.value);
      post({ ...msg, doc: id });
    },
    key: e => handleKey(e, true),
    dropFiles: files => { if (files.length) post({ type: 'drop' }, files); },
  };

  update();
  post({ type: 'ready' });
})();
