using System.Net;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Markdig;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;
using Microsoft.Win32;

namespace MarkdownViewer;

public sealed class MainForm : Form
{
    // Everything is served from a virtual host:
    //   /__shell__.html      the window: tab bar plus one iframe per open file
    //   /__doc__.html?id=N   one document (toolbar, preview, editor)
    //   /__res/...           embedded assets (web/, lib/)
    //   /C:/path/...         local files referenced by a document (images etc.)
    const string Host = "https://md.local";
    const string ShellPath = "/__shell__.html";
    const string DocPath = "/__doc__.html";
    const string ResPrefix = "/__res/";

    static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder()
        .UseAdvancedExtensions()
        .UseEmojiAndSmiley()
        .Build();

    static readonly string[] MarkdownExtensions = [".md", ".markdown", ".mdown", ".mkd", ".mkdn", ".mdx"];

    static readonly Dictionary<string, string> Resources = typeof(MainForm).Assembly.GetManifestResourceNames()
        .ToDictionary(n => n.Replace('\\', '/'), n => n, StringComparer.OrdinalIgnoreCase);

    static bool WindowsIsDark =>
        Registry.GetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize",
            "AppsUseLightTheme", 1) is int v && v == 0;

    static readonly string DataDir =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MarkdownViewer");

    static string SettingsFile => Path.Combine(DataDir, "settings.json");

    static string Version => Updater.CurrentText;

    readonly WebView2 _web = new() { Dock = DockStyle.Fill };
    readonly Dictionary<int, Doc> _docs = [];
    List<int> _order = [];      // tab order, as reported by the shell
    int _nextId, _activeId = -1;
    bool _shellReady, _closeConfirmed;
    CoreWebView2Environment? _env;
    readonly List<string> _startupPaths = [];
    string? _startupActive;
    string _theme = "system";   // "system" (follow Windows), "light" or "dark"
    List<string> _recent = [];  // most recent first
    DateTime? _lastUpdateCheck; // UTC
    string? _skipVersion;       // release tag the user chose to skip
    UpdateInfo? _update;        // newer release found by the last check
    bool _updating;
    readonly Stack<string> _closed = new();  // closed tabs' paths, for Ctrl+Shift+T
    int _untitledCount;
    const int MaxRecent = 12;

    public MainForm(string[] paths)
    {
        Text = "Markdown Viewer";
        Size = new Size(1100, 850);
        StartPosition = FormStartPosition.CenterScreen;
        using (var ico = typeof(MainForm).Assembly.GetManifestResourceStream("app.ico")) if (ico != null) Icon = new Icon(ico);
        Controls.Add(_web);

        var settings = LoadSettings();
        if (settings != null) RestorePlacement(settings);
        if (settings?.Theme is "light" or "dark") _theme = settings.Theme;
        if (settings?.Recent is { } recent) _recent = [.. recent.Take(MaxRecent)];
        _lastUpdateCheck = settings?.LastUpdateCheck;
        _skipVersion = settings?.SkipVersion;
        ApplyTheme();
        SystemEvents.UserPreferenceChanged += OnWindowsThemeChanged;

        // Previous session's tabs first, then files passed on the command line.
        if (settings?.Tabs is { } tabs)
        {
            _startupPaths.AddRange(tabs.Where(File.Exists));
            if (settings.Active >= 0 && settings.Active < tabs.Length) _startupActive = tabs[settings.Active];
        }
        foreach (var p in paths)
        {
            if (!_startupPaths.Contains(p, StringComparer.OrdinalIgnoreCase)) _startupPaths.Add(p);
            _startupActive = p;
        }

        _ = InitAsync();
    }

    async Task InitAsync()
    {
        _env = await CoreWebView2Environment.CreateAsync(null, DataDir);
        await _web.EnsureCoreWebView2Async(_env);

        var core = _web.CoreWebView2;
        core.Settings.IsStatusBarEnabled = false;
        ApplyTheme();
        core.AddWebResourceRequestedFilter(Host + "/*", CoreWebView2WebResourceContext.All);
        core.WebResourceRequested += OnResourceRequested;
        core.NavigationStarting += (_, e) => OnNavigationStarting(e, ShellPath);
        core.FrameNavigationStarting += (_, e) => OnNavigationStarting(e, DocPath);
        core.NewWindowRequested += (_, e) => { e.Handled = true; HandleLink(e.Uri); };
        core.WebMessageReceived += OnWebMessage;
        core.Navigate(Host + ShellPath);
    }

    // ---------- tabs ----------

    Doc? ActiveDoc => _docs.GetValueOrDefault(_activeId);

    /// <summary>Called on the UI thread when another launch of the app passes files.</summary>
    public void OpenFromOtherInstance(string[] paths)
    {
        if (WindowState == FormWindowState.Minimized) WindowState = FormWindowState.Normal;
        Activate();
        SetForegroundWindow(Handle);
        OpenFiles(paths);
    }

    static bool SamePath(string? a, string? b) => a != null && string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    void OpenFiles(IEnumerable<string> paths)
    {
        Doc? last = null;
        foreach (var p in paths)
        {
            var full = Path.GetFullPath(p);
            AddRecent(full);
            last = _docs.Values.FirstOrDefault(d => SamePath(d.Path, full));
            if (last != null) continue;
            last = new Doc(++_nextId, full, this, OnFileChanged);
            _docs[last.Id] = last;
            if (_shellReady) AddTab(last, activate: false);
        }
        if (last != null && _shellReady) Shell($"shell.activate({last.Id})");
    }

    void NewFile()
    {
        var doc = new Doc(++_nextId, null, this, OnFileChanged, $"Untitled-{++_untitledCount}");
        _docs[doc.Id] = doc;
        AddTab(doc, activate: true);
    }

    void AddTab(Doc doc, bool activate) =>
        Shell($"shell.addTab({doc.Id}, {Json(doc.Name)}, {Json(doc.Path ?? "")}, {Json(activate)})");

    async Task<bool> CloseDocAsync(Doc doc)
    {
        if (!await ConfirmDiscardAsync(doc)) return false;
        _docs.Remove(doc.Id);
        if (doc.Path != null) _closed.Push(doc.Path);
        doc.Dispose();
        await Shell($"shell.removeTab({doc.Id})");
        return true;
    }

    void ReopenClosed()
    {
        while (_closed.TryPop(out var path))
        {
            if (_docs.Values.Any(d => SamePath(d.Path, path)) || !File.Exists(path)) continue;
            OpenFiles([path]);
            return;
        }
    }

    // ---------- updates ----------

    /// <summary>Checks GitHub for a newer release shortly after startup, at most once a day.</summary>
    async void ScheduleUpdateCheck()
    {
        if (_lastUpdateCheck is { } last && DateTime.UtcNow - last < TimeSpan.FromHours(20)) return;
        await Task.Delay(4000); // let the window and tabs load first
        if (!IsDisposed) await CheckForUpdatesAsync(manual: false);
    }

    async Task CheckForUpdatesAsync(bool manual)
    {
        UpdateInfo? latest;
        try { latest = await Updater.GetLatestAsync(); }
        catch (Exception ex)
        {
            if (manual) await Shell($"shell.toast({Json($"Couldn't check for updates: {ex.Message}")})");
            return;
        }
        _lastUpdateCheck = DateTime.UtcNow;

        if (latest == null || latest.Version <= Updater.Current)
        {
            _update = null;
            if (manual) await Shell($"shell.toast({Json($"You have the latest version ({Version}).")})");
            return;
        }
        if (!manual && latest.Tag == _skipVersion) return;

        _update = latest;
        var canInstall = Updater.IsInstalled && latest.MsiUrl != null;
        await Shell($"shell.showUpdate({Json(new { version = latest.Version.ToString(), canInstall })})");
    }

    async Task InstallUpdateAsync(UpdateInfo update)
    {
        if (_updating) return;
        if (!Updater.IsInstalled || update.MsiUrl == null)
        {
            ShellOpen(update.PageUrl); // portable copy: download it from the release page
            return;
        }
        if (!await ConfirmDiscardAllAsync()) return;

        _updating = true;
        try
        {
            await Shell($"shell.updateStatus({Json("Downloading update…")}, true)");
            var progress = new Progress<int>(p => _ = Shell($"shell.updateStatus({Json($"Downloading update… {p}%")}, true)"));
            var msi = await Updater.DownloadInstallerAsync(update, progress);

            await Shell($"shell.updateStatus({Json("Installing… the app will restart.")}, true)");
            SaveSettings();
            Updater.InstallAndRestart(msi);
            _closeConfirmed = true;
            Close();
        }
        catch (Exception ex)
        {
            _updating = false;
            await Shell($"shell.updateStatus({Json($"Update failed: {ex.Message}")}, false)");
        }
    }

    // ---------- recent files ----------

    void AddRecent(string path)
    {
        _recent.RemoveAll(p => SamePath(p, path));
        _recent.Insert(0, path);
        if (_recent.Count > MaxRecent) _recent.RemoveRange(MaxRecent, _recent.Count - MaxRecent);
        PushRecent();
    }

    void PushRecent()
    {
        if (_shellReady) Shell($"shell.setRecent({Json(_recent.Where(File.Exists).ToArray())})");
    }

    void UpdateTitle()
    {
        var doc = ActiveDoc;
        Text = doc == null ? "Markdown Viewer" : $"{(doc.Dirty ? "\u25CF " : "")}{doc.Name} - Markdown Viewer";
    }

    Task<string> Shell(string script) => _web.CoreWebView2.ExecuteScriptAsync(script);

    /// <summary>Calls a function defined in a document's iframe.</summary>
    Task<string> CallDoc(Doc doc, string function, params object[] args) =>
        Shell($"shell.call({doc.Id}, {Json(function)}{string.Concat(args.Select(a => ", " + Json(a)))})");

    static string Json(object value) => JsonSerializer.Serialize(value);

    // ---------- file handling ----------

    async void OnFileChanged(Doc doc)
    {
        if (!_docs.ContainsKey(doc.Id)) return;
        var text = doc.Read();
        if (text == null || text == doc.DiskText) return; // our own save, or nothing changed
        doc.DiskText = text;
        await CallDoc(doc, "__external", Normalize(text), Render(text));
    }

    static string Normalize(string text) => text.Replace("\r\n", "\n");

    /// <summary>Saves the document, asking for a file name first if it has none (or for Save As).</summary>
    async Task<bool> SaveDocAsync(Doc doc, string markdown, bool saveAs = false)
    {
        if (saveAs || doc.Path == null)
        {
            var path = AskSavePath(doc);
            if (path == null) return false;
            // The chosen file is open in another tab: that tab goes away (asking first if it has edits).
            var other = _docs.Values.FirstOrDefault(d => d != doc && SamePath(d.Path, path));
            if (other != null && !await CloseDocAsync(other)) return false;
            doc.SetPath(path);
            AddRecent(path);
            await Shell($"shell.renameTab({doc.Id}, {Json(doc.Name)}, {Json(path)})");
            await CallDoc(doc, "__renamed", path, BaseHref(doc));
        }

        try { doc.Write(markdown); }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"Could not save {doc.Name}:\n\n{ex.Message}", "Markdown Viewer",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
            return false;
        }
        UpdateTitle();
        PushRecent(); // a file created by "Save as" exists only now
        await CallDoc(doc, "__saved");
        return true;
    }

    string? AskSavePath(Doc doc)
    {
        using var dlg = new SaveFileDialog
        {
            Filter = "Markdown (*.md)|*.md|Markdown (*.markdown)|*.markdown|Text (*.txt)|*.txt|All files|*.*",
            DefaultExt = "md",
            AddExtension = true,
            FileName = doc.Path != null ? Path.GetFileName(doc.Path) : doc.Name + ".md",
            InitialDirectory = Path.GetDirectoryName(doc.Path ?? ActiveDoc?.Path ?? _recent.FirstOrDefault() ?? "")
                               is { Length: > 0 } dir && Directory.Exists(dir)
                ? dir : Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
        };
        return dlg.ShowDialog(this) == DialogResult.OK ? dlg.FileName : null;
    }

    async Task<bool> ConfirmDiscardAsync(Doc doc)
    {
        if (!doc.Dirty) return true;
        await Shell($"shell.activate({doc.Id})");
        var answer = MessageBox.Show(this, $"Save changes to {doc.Name}?", "Markdown Viewer",
            MessageBoxButtons.YesNoCancel, MessageBoxIcon.Warning);
        if (answer == DialogResult.Cancel) return false;
        if (answer == DialogResult.Yes)
        {
            var json = await CallDoc(doc, "__getMarkdown");
            if (JsonSerializer.Deserialize<string>(json) is not { } md || !await SaveDocAsync(doc, md)) return false;
        }
        doc.Dirty = false;
        return true;
    }

    void ShowOpenDialog()
    {
        using var dlg = new OpenFileDialog
        {
            Filter = "Markdown files|*.md;*.markdown;*.mdown;*.mkd;*.mkdn;*.mdx;*.txt|All files|*.*",
            Multiselect = true,
            InitialDirectory = ActiveDoc?.Path != null ? Path.GetDirectoryName(ActiveDoc.Path) : null,
        };
        if (dlg.ShowDialog(this) == DialogResult.OK) OpenFiles(dlg.FileNames);
    }

    static bool IsMarkdown(string path) =>
        MarkdownExtensions.Contains(Path.GetExtension(path).ToLowerInvariant()) ||
        Path.GetExtension(path).Equals(".txt", StringComparison.OrdinalIgnoreCase);

    // ---------- messages from the page ----------

    async void OnWebMessage(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        using var msg = JsonDocument.Parse(e.WebMessageAsJson);
        var root = msg.RootElement;
        string? Str(string name) => root.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() : null;
        var doc = root.TryGetProperty("doc", out var d) && d.ValueKind == JsonValueKind.Number ? _docs.GetValueOrDefault(d.GetInt32()) : null;

        switch (Str("type"))
        {
            case "ready":
                OpenFiles(_startupPaths);
                _shellReady = true;
                foreach (var x in _docs.Values) AddTab(x, activate: false);
                var first = _docs.Values.FirstOrDefault(x => string.Equals(x.Path, _startupActive, StringComparison.OrdinalIgnoreCase))
                            ?? _docs.Values.LastOrDefault();
                if (first != null) await Shell($"shell.activate({first.Id})");
                PushRecent();
                ScheduleUpdateCheck();
                break;
            case "open":
                ShowOpenDialog();
                break;
            case "new":
                NewFile();
                break;
            case "openPath" when Str("path") is { } path:
                if (File.Exists(path)) OpenFiles([path]);
                else
                {
                    _recent.RemoveAll(p => SamePath(p, path));
                    PushRecent();
                    MessageBox.Show(this, $"This file no longer exists:\n\n{path}", "Markdown Viewer",
                        MessageBoxButtons.OK, MessageBoxIcon.Information);
                }
                break;
            case "checkUpdates":
                await CheckForUpdatesAsync(manual: true);
                break;
            case "updateNotes" when _update != null:
                ShellOpen(_update.PageUrl);
                break;
            case "updateSkip" when _update != null:
                _skipVersion = _update.Tag;
                break;
            case "updateInstall" when _update != null:
                await InstallUpdateAsync(_update);
                break;
            case "clearRecent":
                _recent.Clear();
                PushRecent();
                break;
            case "reopenClosed":
                ReopenClosed();
                break;
            case "closeTabs":
                foreach (var id in root.GetProperty("ids").EnumerateArray().Select(x => x.GetInt32()).ToList())
                    if (_docs.TryGetValue(id, out var target) && !await CloseDocAsync(target)) break;
                break;
            case "copyPath" when doc?.Path != null:
                Clipboard.SetText(doc.Path);
                break;
            case "reveal" when doc?.Path != null:
                ShellOpenWithArgs("explorer.exe", $"/select,\"{doc.Path}\"");
                break;
            case "saveAs" when doc != null:
                await SaveDocAsync(doc, Str("md") ?? "", saveAs: true);
                break;
            case "theme" when Str("value") is "system" or "light" or "dark":
                _theme = Str("value")!;
                ApplyTheme();
                break;
            case "drop":
                OpenFiles(e.AdditionalObjects.OfType<CoreWebView2File>().Select(f => f.Path).Where(IsMarkdown));
                break;
            case "closeWindow":
                Close();
                break;
            case "order":
                _order = root.GetProperty("ids").EnumerateArray().Select(x => x.GetInt32()).ToList();
                break;
            case "active":
                _activeId = doc?.Id ?? -1;
                UpdateTitle();
                break;
            case "closeTab" when doc != null:
                await CloseDocAsync(doc);
                break;
            case "dirty" when doc != null:
                doc.Dirty = root.GetProperty("value").GetBoolean();
                UpdateTitle();
                break;
            case "render" when doc != null:
                await CallDoc(doc, "__update", Render(Str("md") ?? ""));
                break;
            case "save" when doc != null:
                await SaveDocAsync(doc, Str("md") ?? "");
                break;
            case "reload" when doc?.Path != null:
                if (await ConfirmDiscardAsync(doc)) { doc.DiskText = null; await Shell($"shell.reload({doc.Id})"); }
                break;
        }
    }

    // ---------- navigation & links ----------

    void OnNavigationStarting(CoreWebView2NavigationStartingEventArgs e, string allowedPath)
    {
        var uri = new Uri(e.Uri);
        if (uri.Scheme == "about") return;
        if (uri.GetLeftPart(UriPartial.Authority) == Host && uri.AbsolutePath == allowedPath) return;
        e.Cancel = true;
        HandleLink(e.Uri);
    }

    // Links clicked in a document and files dropped onto the window end up here.
    void HandleLink(string link)
    {
        if (!Uri.TryCreate(link, UriKind.Absolute, out var uri)) return;

        string? local = uri.IsFile ? uri.LocalPath
            : uri.GetLeftPart(UriPartial.Authority) == Host ? LocalPathFromUrl(uri)
            : null;

        if (local != null)
        {
            if (IsMarkdown(local) && File.Exists(local))
                OpenFiles([local]);
            else if (File.Exists(local) || Directory.Exists(local))
                ShellOpen(local);
            return;
        }

        if (uri.Scheme is "http" or "https" or "mailto") ShellOpen(uri.AbsoluteUri);
    }

    static void ShellOpen(string target) => ShellOpenWithArgs(target, null);

    static void ShellOpenWithArgs(string target, string? args)
    {
        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(target, args ?? "") { UseShellExecute = true }); }
        catch { /* nothing registered for it */ }
    }

    static string? LocalPathFromUrl(Uri uri)
    {
        var p = Uri.UnescapeDataString(uri.AbsolutePath).TrimStart('/');
        if (p.Length < 3 || p[1] != ':') return null; // expect "C:/..."
        try { return Path.GetFullPath(p); } catch { return null; }
    }

    // ---------- resource serving ----------

    void OnResourceRequested(object? sender, CoreWebView2WebResourceRequestedEventArgs e)
    {
        var uri = new Uri(e.Request.Uri);
        var path = uri.AbsolutePath;

        if (path == ShellPath)
        {
            e.Response = Html(BuildShell());
            return;
        }
        if (path == DocPath)
        {
            var id = int.TryParse(System.Web.HttpUtility.ParseQueryString(uri.Query)["id"], out var n) ? n : -1;
            e.Response = Html(_docs.TryGetValue(id, out var doc) ? BuildDocPage(doc) : "");
            return;
        }
        if (path.StartsWith(ResPrefix) && Resources.TryGetValue(path[ResPrefix.Length..], out var res))
        {
            e.Response = Respond(typeof(MainForm).Assembly.GetManifestResourceStream(res)!, MimeType(res), cache: true);
            return;
        }

        var local = LocalPathFromUrl(uri);
        if (local != null && File.Exists(local))
        {
            try { e.Response = Respond(new MemoryStream(File.ReadAllBytes(local)), MimeType(local), cache: false); return; }
            catch (IOException) { }
        }
        e.Response = _env!.CreateWebResourceResponse(null, 404, "Not Found", "");
    }

    CoreWebView2WebResourceResponse Html(string html) =>
        Respond(new MemoryStream(Encoding.UTF8.GetBytes(html)), "text/html; charset=utf-8", cache: false);

    CoreWebView2WebResourceResponse Respond(Stream data, string contentType, bool cache) =>
        _env!.CreateWebResourceResponse(data, 200, "OK",
            $"Content-Type: {contentType}\r\nCache-Control: {(cache ? "max-age=31536000, immutable" : "no-store")}");

    static string MimeType(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".png" => "image/png",
        ".jpg" or ".jpeg" => "image/jpeg",
        ".gif" => "image/gif",
        ".svg" => "image/svg+xml",
        ".webp" => "image/webp",
        ".bmp" => "image/bmp",
        ".ico" => "image/x-icon",
        ".avif" => "image/avif",
        ".mp4" => "video/mp4",
        ".webm" => "video/webm",
        ".css" => "text/css",
        ".js" => "text/javascript",
        ".woff2" => "font/woff2",
        ".pdf" => "application/pdf",
        _ => "application/octet-stream",
    };

    // ---------- pages ----------

    static string Render(string markdown) => Markdown.ToHtml(markdown, Pipeline);

    static string Csp(string scripts) =>
        $"""<meta http-equiv="Content-Security-Policy" content="default-src {Host} data: blob:; img-src * data: blob:; media-src * data: blob:; style-src {Host} 'unsafe-inline'; script-src {scripts}; object-src 'none'">""";

    string BuildShell() => $$"""
        <!doctype html>
        <html>
        <head>
        <meta charset="utf-8">
        {{Csp(Host + ResPrefix)}}
        <meta name="color-scheme" content="light dark">
        <title>Markdown Viewer</title>
        <link rel="stylesheet" href="{{ResPrefix}}web/app.css">
        </head>
        <body class="shell" data-theme="{{_theme}}" data-version="{{Version}}">
        <nav id="tabs">
          <div id="tablist" role="tablist"></div>
          <button id="new" class="tool" title="New file (Ctrl+N)">+</button>
          <button id="open-menu" class="tool" title="Open file or a recent one (Ctrl+O)">
            <svg viewBox="0 0 24 24" width="16" height="16" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><path d="M3 7a2 2 0 0 1 2-2h4l2 2h8a2 2 0 0 1 2 2v8a2 2 0 0 1-2 2H5a2 2 0 0 1-2-2z"/></svg>
            <svg viewBox="0 0 24 24" width="10" height="10" fill="none" stroke="currentColor" stroke-width="3" stroke-linecap="round" stroke-linejoin="round"><path d="M6 9l6 6 6-6"/></svg>
          </button>
          <span class="spacer"></span>
          <button id="theme"></button>
        </nav>
        <div id="update" hidden>
          <span id="update-text"></span>
          <button id="update-install" class="primary"></button>
          <button id="update-notes">What's new</button>
          <button id="update-skip">Skip this version</button>
          <button id="update-close" class="x" title="Remind me later">×</button>
        </div>
        <div id="frames">
        <div id="empty" class="welcome">
          <h1>Markdown Viewer</h1>
          <p>Drop Markdown files here, or</p>
          <p class="actions"><button id="new-empty">New file</button> <button id="open-empty">Open files…</button></p>
          <div id="recent" hidden><h2>Recent</h2><ul></ul></div>
          <p class="hint">Ctrl+N new · Ctrl+O open · Ctrl+S save · Ctrl+Shift+S save as · Ctrl+1 / 2 / 3 preview · markdown · visual<br>
             Ctrl+Tab next tab · Ctrl+W close tab · Ctrl+Shift+T reopen closed tab · Ctrl+Shift+O outline · Ctrl+F find · Ctrl +/− zoom</p>
          <p class="version">Version {{Version}}</p>
        </div>
        </div>
        <div id="toast" hidden></div>
        <script src="{{ResPrefix}}web/shell.js"></script>
        </body>
        </html>
        """;

    /// <summary>Base URL for a document's relative links: "/C:/some%20dir/".</summary>
    static string BaseHref(Doc doc) => doc.Path == null ? "/"
        : "/" + new Uri(Path.GetDirectoryName(doc.Path)!.TrimEnd('\\') + "\\").AbsolutePath.TrimStart('/');

    string BuildDocPage(Doc doc)
    {
        string body = "";
        string? md = null;
        var isNew = doc.Path == null;

        if (isNew)
        {
            md = "";
        }
        else if ((doc.DiskText = doc.Read()) is { } text)
        {
            md = Normalize(text);
            body = Render(text);
        }
        else
        {
            body = $"<div class=\"welcome\"><h1>File not found</h1><p>{WebUtility.HtmlEncode(doc.Path)}</p></div>";
        }

        var baseHref = BaseHref(doc);
        var init = Json(new
        {
            id = doc.Id, md = md ?? "", editable = md != null, isNew,
            path = doc.Path ?? "", name = doc.Name, crlf = doc.Crlf, bom = doc.Bom,
        });

        return $$"""
            <!doctype html>
            <html>
            <head>
            <meta charset="utf-8">
            <base href="{{Host}}{{baseHref}}">
            {{Csp(Host + ResPrefix)}}
            <meta name="color-scheme" content="light dark">
            <title>{{WebUtility.HtmlEncode(doc.Name)}}</title>
            <link rel="stylesheet" href="{{ResPrefix}}web/app.css">
            <link rel="stylesheet" media="(prefers-color-scheme: light)" href="{{ResPrefix}}lib/hljs/github.min.css">
            <link rel="stylesheet" media="(prefers-color-scheme: dark)" href="{{ResPrefix}}lib/hljs/github-dark.min.css">
            </head>
            <body>
            <header id="bar">
              <button id="outline-btn" class="icon" title="Outline (Ctrl+Shift+O)">
                <svg viewBox="0 0 24 24" width="16" height="16" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round"><path d="M4 6h16M8 12h12M8 18h12M4 12h.01M4 18h.01"/></svg>
              </button>
              <div class="seg">
                <button data-mode="preview" class="on" title="Preview (Ctrl+1)">Preview</button>
                <button data-mode="markdown" title="Edit Markdown source (Ctrl+2)">Markdown</button>
                <button data-mode="visual" title="Edit visually (Ctrl+3)">Visual</button>
              </div>
              <span id="name"></span>
              <button id="save" title="Save (Ctrl+S)" disabled>Save</button>
            </header>
            <div id="banner" hidden>
              <span>This file was changed outside the app.</span>
              <button id="b-load">Load new version</button>
              <button id="b-keep">Keep my edits</button>
            </div>
            <div id="main">
              <aside id="outline" hidden><div class="ol-title">Outline</div><nav id="ol-list"></nav></aside>
              <main id="preview"><article id="content" class="markdown-body">{{body}}</article></main>
              <div id="editor" hidden></div>
            </div>
            <footer id="status"><span id="st-left"></span><span id="st-right"></span></footer>
            <script type="application/json" id="init">{{init}}</script>
            <script src="{{ResPrefix}}web/app.js"></script>
            </body>
            </html>
            """;
    }

    // ---------- window & session ----------

    sealed record Settings(int X, int Y, int Width, int Height, bool Maximized, string[]? Tabs, int Active,
        string? Theme = null, string[]? Recent = null, DateTime? LastUpdateCheck = null, string? SkipVersion = null);

    static Settings? LoadSettings()
    {
        try { return File.Exists(SettingsFile) ? JsonSerializer.Deserialize<Settings>(File.ReadAllText(SettingsFile)) : null; }
        catch { return null; } // corrupt settings: use defaults
    }

    void RestorePlacement(Settings s)
    {
        var rect = new Rectangle(s.X, s.Y, s.Width, s.Height);
        if (!Screen.AllScreens.Any(sc => sc.WorkingArea.IntersectsWith(rect))) return;
        StartPosition = FormStartPosition.Manual;
        Bounds = rect;
        if (s.Maximized) WindowState = FormWindowState.Maximized;
    }

    void SaveSettings()
    {
        try
        {
            var b = WindowState == FormWindowState.Normal ? Bounds : RestoreBounds;
            // Untitled (never saved) tabs are not part of the session.
            var ids = _order.Where(_docs.ContainsKey).Concat(_docs.Keys.Except(_order))
                .Where(id => _docs[id].Path != null).ToList();
            var tabs = ids.Select(id => _docs[id].Path!).ToArray();
            Directory.CreateDirectory(DataDir);
            File.WriteAllText(SettingsFile, Json(new Settings(b.X, b.Y, b.Width, b.Height,
                WindowState == FormWindowState.Maximized, tabs, ids.IndexOf(_activeId), _theme, [.. _recent],
                _lastUpdateCheck, _skipVersion)));
        }
        catch { }
    }

    async Task<bool> ConfirmDiscardAllAsync()
    {
        foreach (var doc in _docs.Values.Where(d => d.Dirty).ToList())
            if (!await ConfirmDiscardAsync(doc)) return false;
        return true;
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (!_closeConfirmed && _docs.Values.Any(d => d.Dirty))
        {
            e.Cancel = true;
            BeginInvoke(async () =>
            {
                if (!await ConfirmDiscardAllAsync()) return;
                _closeConfirmed = true;
                Close();
            });
            return;
        }
        SaveSettings();
        base.OnFormClosing(e);
    }

    [DllImport("dwmapi.dll")]
    static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    [DllImport("user32.dll")]
    static extern bool SetForegroundWindow(IntPtr hwnd);

    // ---------- theme ----------

    bool IsDark => _theme == "dark" || (_theme == "system" && WindowsIsDark);

    /// <summary>
    /// Pages style themselves with prefers-color-scheme, so the WebView2 profile's preferred
    /// scheme switches every tab at once; the native title bar and background follow here.
    /// </summary>
    void ApplyTheme()
    {
        var dark = IsDark;
        var bg = dark ? Color.FromArgb(13, 17, 23) : Color.White;
        BackColor = bg;
        _web.DefaultBackgroundColor = bg;
        if (IsHandleCreated)
        {
            int value = dark ? 1 : 0;
            DwmSetWindowAttribute(Handle, 20 /* DWMWA_USE_IMMERSIVE_DARK_MODE */, ref value, sizeof(int));
        }
        if (_web.CoreWebView2 is { } core)
            core.Profile.PreferredColorScheme = _theme switch
            {
                "light" => CoreWebView2PreferredColorScheme.Light,
                "dark" => CoreWebView2PreferredColorScheme.Dark,
                _ => CoreWebView2PreferredColorScheme.Auto,
            };
    }

    void OnWindowsThemeChanged(object? sender, UserPreferenceChangedEventArgs e)
    {
        if (_theme == "system" && !IsDisposed) BeginInvoke(ApplyTheme);
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        ApplyTheme();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) SystemEvents.UserPreferenceChanged -= OnWindowsThemeChanged;
        if (disposing) foreach (var doc in _docs.Values) doc.Dispose();
        base.Dispose(disposing);
    }
}
