using MarkdownViewer;
using Xunit;

public class RendererTests
{
    [Fact]
    public void Renders_github_tables()
    {
        var html = MarkdownRenderer.Render("| A | B |\n|---|:-:|\n| 1 | 2 |\n");
        Assert.Contains("<table>", html);
        Assert.Contains("<th>A</th>", html);
        Assert.Contains("<td style=\"text-align: center;\">2</td>", html);
    }

    [Fact]
    public void Renders_task_lists_as_checkboxes()
    {
        var html = MarkdownRenderer.Render("- [x] done\n- [ ] todo\n");
        Assert.Contains("task-list-item", html);
        Assert.Contains("checked=\"checked\"", html);
    }

    [Fact]
    public void Renders_footnotes()
    {
        var html = MarkdownRenderer.Render("Text.[^1]\n\n[^1]: The note.\n");
        Assert.Contains("class=\"footnotes\"", html);
        Assert.Contains("The note.", html);
    }

    [Fact]
    public void Marks_mermaid_blocks_for_the_diagram_renderer()
    {
        var html = MarkdownRenderer.Render("```mermaid\ngraph LR\n  A --> B\n```\n");
        Assert.Contains("class=\"mermaid\"", html);
    }

    [Fact]
    public void Marks_math_for_katex()
    {
        var html = MarkdownRenderer.Render("Inline $x^2$ and\n\n$$\nE = mc^2\n$$\n");
        Assert.Contains("class=\"math\"", html);
    }

    [Fact]
    public void Gives_headings_ids_for_links_and_the_outline()
    {
        var html = MarkdownRenderer.Render("## Getting started\n");
        Assert.Contains("<h2 id=\"getting-started\">", html);
    }

    [Fact]
    public void Converts_emoji_shortcodes()
    {
        Assert.Contains("😄", MarkdownRenderer.Render(":smile:"));
    }
}

public class FrontMatterAndAlertTests
{
    [Fact]
    public void Front_matter_becomes_a_properties_table_not_a_heading()
    {
        var html = MarkdownRenderer.Render("---\ntitle: \"Hello <world>\"\ntags: [a, b]\nlinks:\n  - https://example.com\n---\n\n# Doc\n");
        Assert.StartsWith("<div class=\"front-matter\">", html);
        Assert.Contains("<th>title</th><td>Hello &lt;world&gt;</td>", html);
        Assert.Contains("<span class=\"fm-item\">a</span><span class=\"fm-item\">b</span>", html);
        Assert.Contains("<a href=\"https://example.com/\">https://example.com</a>", html);
        Assert.DoesNotContain("<h2", html);
        Assert.Contains("<h1 id=\"doc\">Doc</h1>", html);
    }

    [Fact]
    public void Complex_front_matter_is_shown_as_yaml()
    {
        var html = MarkdownRenderer.Render("---\nauthor:\n  name: Ann\n  mail: a@b.c\n---\nText\n");
        Assert.StartsWith("<pre class=\"front-matter\"><code class=\"language-yaml\">", html);
        Assert.Contains("  name: Ann", html);
    }

    [Fact]
    public void A_thematic_break_later_is_not_front_matter()
    {
        var html = MarkdownRenderer.Render("Intro\n\n---\n\nMore\n");
        Assert.DoesNotContain("front-matter", html);
        Assert.Contains("<hr />", html);
    }

    [Fact]
    public void Renders_github_alerts()
    {
        var html = MarkdownRenderer.Render("> [!WARNING]\n> Careful.\n");
        Assert.Contains("markdown-alert markdown-alert-warning", html);
        Assert.Contains("Warning</p>", html);
    }
}

public sealed class PastedImagesTests : IDisposable
{
    readonly string _dir = Directory.CreateTempSubdirectory("mv-images-").FullName;
    public void Dispose() => Directory.Delete(_dir, true);

    [Fact]
    public void Pasted_screenshot_is_named_after_the_document_and_time()
    {
        var doc = Path.Combine(_dir, "My notes.md");
        var now = new DateTime(2026, 10, 24, 15, 30, 12);
        Assert.Equal("images/My-notes-20261024-153012.png", PastedImages.Save(doc, "image.png", "image/png", [1, 2, 3], now));
        Assert.Equal("images/My-notes-20261024-153012-2.png", PastedImages.Save(doc, "image.png", "image/png", [4], now));
        Assert.Equal([1, 2, 3], File.ReadAllBytes(Path.Combine(_dir, "images", "My-notes-20261024-153012.png")));
    }

    [Fact]
    public void Dropped_file_keeps_a_cleaned_name()
    {
        var doc = Path.Combine(_dir, "a.md");
        Assert.Equal("images/diagram-v2-final.jpg", PastedImages.Save(doc, "diagram v2 (final).jpeg", "image/jpeg", [1], DateTime.Now));
    }

    [Fact]
    public void Unsupported_types_are_refused()
    {
        Assert.Null(PastedImages.Save(Path.Combine(_dir, "a.md"), "x.pdf", "application/pdf", [1], DateTime.Now));
        Assert.False(Directory.Exists(Path.Combine(_dir, "images")));
    }
}

public sealed class CopyLinkedImagesTests : IDisposable
{
    readonly string _root = Directory.CreateTempSubdirectory("mv-move-").FullName;
    string From => Path.Combine(_root, "old");
    string To => Path.Combine(_root, "new");
    public void Dispose() => Directory.Delete(_root, true);

    public CopyLinkedImagesTests()
    {
        Directory.CreateDirectory(Path.Combine(From, "images"));
        Directory.CreateDirectory(Path.Combine(_root, "shared"));
        Directory.CreateDirectory(To);
        File.WriteAllBytes(Path.Combine(From, "images", "a b.png"), [1]);
        File.WriteAllBytes(Path.Combine(From, "images", "ref.png"), [2]);
        File.WriteAllBytes(Path.Combine(From, "html.png"), [3]);
        File.WriteAllBytes(Path.Combine(_root, "shared", "logo.png"), [4]);
    }

    [Fact]
    public void Copies_relative_images_including_reference_and_html_ones()
    {
        var md = "![a](images/a%20b.png)\n![r][ref]\n<img src=\"html.png\" width=\"20\">\n" +
                 "![web](https://example.com/x.png) ![gone](images/missing.png) [not an image](images/ref.png)\n\n[ref]: images/ref.png\n";
        var result = PastedImages.CopyLinked(md, From, To);
        Assert.Equal(3, result.Copied);
        Assert.Empty(result.Outside);
        Assert.Equal([1], File.ReadAllBytes(Path.Combine(To, "images", "a b.png")));
        Assert.True(File.Exists(Path.Combine(To, "images", "ref.png")));
        Assert.True(File.Exists(Path.Combine(To, "html.png")));
        Assert.False(File.Exists(Path.Combine(To, "images", "missing.png")));
    }

    [Fact]
    public void Leaves_existing_files_alone()
    {
        Directory.CreateDirectory(Path.Combine(To, "images"));
        File.WriteAllBytes(Path.Combine(To, "images", "a b.png"), [9]);
        Assert.Equal(0, PastedImages.CopyLinked("![a](images/a%20b.png)", From, To).Copied);
        Assert.Equal([9], File.ReadAllBytes(Path.Combine(To, "images", "a b.png")));
    }

    [Fact]
    public void Reports_images_that_would_land_outside_the_new_folder()
    {
        var deeper = Path.Combine(_root, "x", "y");
        Directory.CreateDirectory(deeper);
        var result = PastedImages.CopyLinked("![logo](../shared/logo.png)", From, deeper);
        Assert.Equal(0, result.Copied);
        Assert.Equal([Path.Combine("..", "shared", "logo.png")], result.Outside);
        // a sibling folder still reaches the same file: nothing to do, nothing to report
        Assert.Empty(PastedImages.CopyLinked("![logo](../shared/logo.png)", From, To).Outside);
    }
}
