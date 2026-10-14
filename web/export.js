// Runs in the hidden export view (/__export.html): renders code, diagrams and math, and for
// HTML export makes the page self-contained. Then tells the app it's ready to save or print.
(async () => {
  const C = document.getElementById('content');
  const format = new URLSearchParams(location.search).get('format');
  const load = src => new Promise((ok, fail) => {
    const s = document.createElement('script');
    s.src = '/__res/lib/' + src; s.onload = ok; s.onerror = fail;
    document.head.append(s);
  });

  try {
    const codes = C.querySelectorAll('pre > code');
    if (codes.length) {
      await load('hljs/highlight.min.js');
      codes.forEach(c => { if (!c.closest('.mermaid')) hljs.highlightElement(c); });
    }
  } catch {}
  try {
    const diagrams = C.querySelectorAll('.mermaid');
    if (diagrams.length) {
      await load('mermaid/mermaid.min.js');
      mermaid.initialize({ startOnLoad: false, theme: 'default' });
      await mermaid.run({ nodes: diagrams });
    }
  } catch {}
  try {
    if (C.querySelector('.math')) {
      await load('katex/katex.min.js');
      await load('katex/contrib/auto-render.min.js');
      renderMathInElement(C, { throwOnError: false, delimiters: [
        { left: '\\[', right: '\\]', display: true }, { left: '\\(', right: '\\)', display: false },
        { left: '$$', right: '$$', display: true } ] });
    }
  } catch {}

  if (format === 'html') {
    // Embed local images, so the file still shows them when moved or sent to someone.
    for (const img of C.querySelectorAll('img')) {
      try {
        const blob = await (await fetch(img.src)).blob();
        img.src = await new Promise(resolve => {
          const reader = new FileReader();
          reader.onload = () => resolve(reader.result);
          reader.readAsDataURL(blob);
        });
      } catch { /* remote image the page can't read: keep its URL */ }
    }
    // Links stay relative to the document's folder; the viewer-only parts go.
    document.querySelectorAll('script, base, meta[http-equiv]').forEach(n => n.remove());
  }

  chrome.webview.postMessage('export-ready');
})();
