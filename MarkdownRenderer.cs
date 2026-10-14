using Markdig;

namespace MarkdownViewer;

/// <summary>Markdown to HTML: GitHub-style extensions, math, Mermaid blocks, footnotes, emoji.</summary>
static class MarkdownRenderer
{
    static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder()
        .UseAdvancedExtensions()
        .UseEmojiAndSmiley()
        .Build();

    public static string Render(string markdown) => Markdown.ToHtml(markdown, Pipeline);
}
