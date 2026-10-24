using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace MarkdownViewer;

// Export to PDF / HTML and printing. A hidden second WebView renders the document on its own
// page (/__export.html) in a light, print-friendly style, whatever the app's theme is.
public sealed partial class MainForm
{
    const string ExportPath = "/__export.html";

    WebView2? _exportWeb;
    TaskCompletionSource<bool>? _exportReady;
    Doc? _exportDoc;
    string _exportMarkdown = "";
    bool _exporting;

    async Task ExportAsync(Doc doc, string format)
    {
        if (_exporting) return;
        var markdown = JsonSerializer.Deserialize<string>(await CallDoc(doc, "__getMarkdown")) ?? ""; // includes unsaved edits

        string? target = null;
        if (format != "print")
        {
            target = AskExportPath(doc, format);
            if (target == null) return;
        }

        _exporting = true;
        try
        {
            var view = await ExportViewAsync();
            _exportDoc = doc;
            _exportMarkdown = markdown;
            _exportReady = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            view.Navigate($"{Host}{ExportPath}?format={format}&t={DateTime.Now.Ticks}");
            if (await Task.WhenAny(_exportReady.Task, Task.Delay(30000)) != _exportReady.Task)
                throw new TimeoutException("The document took too long to render.");

            switch (format)
            {
                case "pdf":
                    if (!await view.PrintToPdfAsync(target, PdfSettings()))
                        throw new IOException("The PDF could not be written.");
                    await Toast($"Exported {Path.GetFileName(target)}");
                    break;
                case "html":
                    var html = JsonSerializer.Deserialize<string>(await view.ExecuteScriptAsync("document.documentElement.outerHTML"));
                    await File.WriteAllTextAsync(target!, "<!doctype html>\n" + html, new UTF8Encoding(false));
                    await Toast($"Exported {Path.GetFileName(target)}");
                    break;
                case "print":
                    view.ShowPrintUI(CoreWebView2PrintDialogKind.System);
                    break;
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"Could not {(format == "print" ? "print" : "export")} {doc.Name}:\n\n{ex.Message}",
                "Markdown Viewer", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            _exporting = false;
        }
    }

    Task Toast(string text) => Shell($"shell.toast({Json(text)})");

    string? AskExportPath(Doc doc, string format)
    {
        var ext = format == "pdf" ? ".pdf" : ".html";
        using var dlg = new SaveFileDialog
        {
            Title = format == "pdf" ? "Export as PDF" : "Export as HTML",
            Filter = format == "pdf" ? "PDF document (*.pdf)|*.pdf" : "Web page (*.html)|*.html",
            DefaultExt = ext.TrimStart('.'),
            AddExtension = true,
            FileName = Path.GetFileNameWithoutExtension(doc.Name) + ext,
            InitialDirectory = doc.Path != null ? Path.GetDirectoryName(doc.Path)
                : Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
        };
        return dlg.ShowDialog(this) == DialogResult.OK ? dlg.FileName : null;
    }

    CoreWebView2PrintSettings PdfSettings()
    {
        var settings = _env!.CreatePrintSettings();
        var metric = RegionInfo.CurrentRegion.IsMetric;
        settings.PageWidth = metric ? 8.27 : 8.5;   // A4 or Letter, in inches
        settings.PageHeight = metric ? 11.69 : 11;
        settings.MarginTop = settings.MarginBottom = 0.6;
        settings.MarginLeft = settings.MarginRight = 0.7;
        settings.ShouldPrintBackgrounds = true;
        settings.ShouldPrintHeaderAndFooter = false;
        return settings;
    }

    /// <summary>The hidden view used for exporting and printing, created on first use.</summary>
    async Task<CoreWebView2> ExportViewAsync()
    {
        if (_exportWeb?.CoreWebView2 is { } existing) return existing;

        _exportWeb = new WebView2 { Visible = false, Size = new Size(1000, 800), DefaultBackgroundColor = Color.White };
        Controls.Add(_exportWeb);
        _exportWeb.SendToBack();
        await _exportWeb.EnsureCoreWebView2Async(_env);

        var core = _exportWeb.CoreWebView2;
        core.AddWebResourceRequestedFilter(Host + "/*", CoreWebView2WebResourceContext.All);
        core.WebResourceRequested += OnResourceRequested;
        core.NavigationStarting += (_, e) =>
        {
            if (!e.Uri.StartsWith(Host + ExportPath, StringComparison.Ordinal)) e.Cancel = true;
        };
        core.NewWindowRequested += (_, e) => e.Handled = true;
        core.WebMessageReceived += (_, e) =>
        {
            if (e.TryGetWebMessageAsString() == "export-ready") _exportReady?.TrySetResult(true);
        };
        return core;
    }

    /// <summary>
    /// The page the export view renders: the document with all styles inlined (light colors,
    /// print rules, and KaTeX fonts embedded when there is math), so HTML export is one file.
    /// </summary>
    string BuildExportPage()
    {
        var doc = _exportDoc!;
        var body = Render(_exportMarkdown);
        var title = WebUtility.HtmlEncode(Path.GetFileNameWithoutExtension(doc.Name));
        var css = new StringBuilder()
            .Append(ResourceText("web/app.css")).Append('\n')
            .Append(ResourceText("lib/hljs/github.min.css")).Append('\n')
            .Append(ExportCss);
        if (body.Contains("class=\"math\"")) css.Append('\n').Append(KatexCssWithFonts());

        // The doctype matters: without it the page renders in quirks mode, where KaTeX refuses to run.
        // (outerHTML doesn't include it, so HTML export adds it again when saving.)
        return $$"""
            <!doctype html>
            <html lang="en">
            <head>
            <meta charset="utf-8">
            <meta name="viewport" content="width=device-width, initial-scale=1">
            <meta name="color-scheme" content="light">
            <meta name="generator" content="Markdown Viewer {{Version}}">
            <base href="{{Host}}{{BaseHref(doc)}}">
            {{Csp(Host + ResPrefix)}}
            <title>{{title}}</title>
            <style>
            {{css}}
            </style>
            </head>
            <body class="export">
            <article id="content" class="markdown-body">
            {{body}}
            </article>
            <script src="{{ResPrefix}}web/export.js"></script>
            </body>
            </html>
            """;
    }

    // Light colors regardless of the app's theme, a normal scrolling page, and print rules.
    const string ExportCss = """
        :root { --fg:#1f2328; --muted:#59636e; --bg:#ffffff; --bar:#f6f8fa; --border:#d1d9e0; --code-bg:#f6f8fa; --link:#0969da; --accent:#0969da;
                --alert-note:#0969da; --alert-tip:#1a7f37; --alert-important:#8250df; --alert-warning:#9a6700; --alert-caution:#d1242f; }
        html, body { height: auto; }
        body.export { display: block; background: #fff; color: var(--fg); }
        body.export .markdown-body { padding: 32px 24px 48px; }
        @media print {
          body.export .markdown-body { max-width: none; padding: 0; }
          pre, table, img, .mermaid, blockquote, .math { break-inside: avoid; }
          h1, h2, h3, h4, h5, h6 { break-after: avoid; }
          a { color: inherit; }
        }
        """;

    string KatexCssWithFonts()
    {
        var css = ResourceText("lib/katex/katex.min.css");
        return Regex.Replace(css, @"url\(fonts/([\w-]+\.woff2)\)", m =>
        {
            var key = "lib/katex/fonts/" + m.Groups[1].Value;
            if (!Resources.TryGetValue(key, out var name)) return m.Value;
            using var stream = typeof(MainForm).Assembly.GetManifestResourceStream(name)!;
            using var ms = new MemoryStream();
            stream.CopyTo(ms);
            return $"url(data:font/woff2;base64,{Convert.ToBase64String(ms.ToArray())})";
        });
    }

    static string ResourceText(string key)
    {
        using var stream = typeof(MainForm).Assembly.GetManifestResourceStream(Resources[key])!;
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
