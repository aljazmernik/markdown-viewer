using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using Markdig;
using Markdig.Extensions.Yaml;
using Markdig.Syntax;

namespace MarkdownViewer;

/// <summary>Markdown to HTML: GitHub-style extensions (including alerts), math, Mermaid blocks,
/// footnotes, emoji, and YAML front matter shown as a properties table.</summary>
static class MarkdownRenderer
{
    static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder()
        .UseAdvancedExtensions()   // includes GitHub alerts: > [!NOTE]
        .UseYamlFrontMatter()      // a leading --- block is metadata, not a heading
        .UseEmojiAndSmiley()
        .Build();

    public static string Render(string markdown)
    {
        var document = Markdown.Parse(markdown, Pipeline);
        var html = document.ToHtml(Pipeline);
        return document.Descendants<YamlFrontMatterBlock>().FirstOrDefault() is { } front
            ? FrontMatterHtml(front.Lines.ToString()) + html
            : html;
    }

    /// <summary>Local images the document links with a relative path ("images/a b.png" for
    /// ![](images/a%20b.png) or &lt;img src="…"&gt;), unescaped, without ?query or #fragment.</summary>
    internal static IEnumerable<string> RelativeImagePaths(string markdown)
    {
        var urls = Markdown.Parse(markdown, Pipeline).Descendants<Markdig.Syntax.Inlines.LinkInline>()
            .Where(l => l.IsImage).Select(l => l.Url)
            .Concat(Regex.Matches(markdown, @"<img\b[^>]*?\bsrc\s*=\s*[""']([^""']+)[""']", RegexOptions.IgnoreCase).Select(m => m.Groups[1].Value));
        foreach (var url in urls)
        {
            if (string.IsNullOrWhiteSpace(url) || url[0] is '/' or '\\' or '#' || Uri.TryCreate(url, UriKind.Absolute, out _)) continue;
            var path = Uri.UnescapeDataString(url.Split('?', '#')[0]).Replace('/', Path.DirectorySeparatorChar);
            if (path != "") yield return path;
        }
    }

    // ---------- front matter ----------

    /// <summary>Simple YAML (key: value, [a, b] and "- item" lists) as a table; anything more
    /// complex (nested maps, multi-line strings) as a YAML code block.</summary>
    internal static string FrontMatterHtml(string yaml)
    {
        var rows = new List<(string Key, List<string> Values, bool List)>();
        foreach (var line in yaml.Replace("\r", "").Split('\n'))
        {
            var trimmed = line.Trim();
            if (trimmed is "" or "---" or "..." || trimmed.StartsWith('#')) continue;

            var item = Regex.Match(line, @"^\s*-\s+(.*)$");
            if (item.Success && rows.Count > 0 && rows[^1].List)
            {
                rows[^1].Values.Add(Scalar(item.Groups[1].Value));
                continue;
            }

            var pair = Regex.Match(line, @"^([^\s:#\-][^:]*):(?:\s+(.*))?$");
            if (!pair.Success) return RawFrontMatter(yaml);
            var key = pair.Groups[1].Value.Trim();
            var value = pair.Groups[2].Value.Trim();
            if (value is "|" or ">" or "|-" or ">-" || value.StartsWith('{')) return RawFrontMatter(yaml);

            if (value == "") rows.Add((key, [], true));   // a "- item" list may follow
            else if (value.StartsWith('[') && value.EndsWith(']'))
                rows.Add((key, value[1..^1].Split(',').Select(Scalar).Where(v => v != "").ToList(), true));
            else rows.Add((key, [Scalar(value)], false));
        }
        if (rows.Count == 0) return "";

        var sb = new StringBuilder("<div class=\"front-matter\"><table>");
        foreach (var (key, values, list) in rows)
        {
            sb.Append("<tr><th>").Append(Encode(key)).Append("</th><td>");
            if (list) foreach (var v in values) sb.Append("<span class=\"fm-item\">").Append(Value(v)).Append("</span>");
            else sb.Append(Value(values[0]));
            sb.Append("</td></tr>");
        }
        return sb.Append("</table></div>\n").ToString();
    }

    static string RawFrontMatter(string yaml) =>
        $"<pre class=\"front-matter\"><code class=\"language-yaml\">{Encode(yaml.Trim('\r', '\n'))}</code></pre>\n";

    /// <summary>A YAML scalar without its quotes.</summary>
    static string Scalar(string s)
    {
        s = s.Trim();
        return s.Length >= 2 && (s[0] == '"' && s[^1] == '"' || s[0] == '\'' && s[^1] == '\'') ? s[1..^1] : s;
    }

    static string Value(string v) =>
        Uri.TryCreate(v, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https"
            ? $"<a href=\"{Encode(uri.AbsoluteUri)}\">{Encode(v)}</a>"
            : Encode(v);

    static string Encode(string s) => WebUtility.HtmlEncode(s);
}
