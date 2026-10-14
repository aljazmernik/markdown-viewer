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
