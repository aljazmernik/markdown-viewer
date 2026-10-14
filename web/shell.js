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
      ...(recent.length ? ['-'] : []),
      ...recent.map(p => ({ label: fileName(p), sub: dirName(p), title: p, action: () => post({ type: 'openPath', path: p }) })),
      ...(recent.length ? ['-', { label: 'Clear recent files', action: () => post({ type: 'clearRecent' }) }] : []),
    ], r.left, r.bottom + 2);
  }
  const update = () => { $('empty').hidden = tabs.size > 0; document.body.classList.toggle('has-tabs', tabs.size > 0); };

  // Shared shortcuts; documents forward theirs here too.
  function handleKey(e, fromDoc) {
    const k = e.key.toLowerCase(), ctrl = e.ctrlKey && !e.altKey;
    if (k === 'escape' && menu) closeMenu();
    else if (ctrl && k === 'tab') cycle(e.shiftKey ? -1 : 1);
    else if (ctrl && e.shiftKey && k === 't') post({ type: 'reopenClosed' });
    else if (ctrl && !e.shiftKey && k === 'n') post({ type: 'new' });
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

  // Theme: System (follow Windows) -> Light -> Dark. The app applies it to every tab.
  const THEMES = {
    system: { next: 'light', label: 'Theme: System (follows Windows)',
      svg: '<circle cx="12" cy="12" r="8"/><path d="M12 4a8 8 0 0 1 0 16z" fill="currentColor" stroke="none"/>' },
    light: { next: 'dark', label: 'Theme: Light',
      svg: '<circle cx="12" cy="12" r="4"/><path d="M12 2v2M12 20v2M4.9 4.9l1.4 1.4M17.7 17.7l1.4 1.4M2 12h2M20 12h2M4.9 19.1l1.4-1.4M17.7 6.3l1.4-1.4"/>' },
    dark: { next: 'system', label: 'Theme: Dark',
      svg: '<path d="M20 14.5A8 8 0 0 1 9.5 4a8 8 0 1 0 10.5 10.5z"/>' },
  };
  function showTheme(name) {
    const t = THEMES[name] ?? THEMES.system;
    document.body.dataset.theme = name;
    $('theme').title = `${t.label} — click to change`;
    $('theme').setAttribute('aria-label', t.label);
    $('theme').innerHTML = `<svg viewBox="0 0 24 24" width="16" height="16" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round">${t.svg}</svg>`;
  }
  $('theme').addEventListener('click', () => {
    const next = (THEMES[document.body.dataset.theme] ?? THEMES.system).next;
    showTheme(next);
    post({ type: 'theme', value: next });
  });
  showTheme(document.body.dataset.theme);

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
