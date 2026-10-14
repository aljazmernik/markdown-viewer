using System.Text;
using MarkdownViewer;
using Xunit;

public sealed class DocTests : IDisposable
{
    readonly string _dir = Directory.CreateTempSubdirectory("mv-tests-").FullName;
    readonly Control _owner = new();

    public void Dispose()
    {
        _owner.Dispose();
        Directory.Delete(_dir, recursive: true);
    }

    Doc Open(string name, byte[] content)
    {
        var path = Path.Combine(_dir, name);
        File.WriteAllBytes(path, content);
        return new Doc(1, path, _owner, _ => { });
    }

    [Fact]
    public void Keeps_crlf_line_endings_when_saving()
    {
        using var doc = Open("crlf.md", Encoding.UTF8.GetBytes("# Title\r\n\r\nText\r\n"));
        Assert.NotNull(doc.Read());
        Assert.True(doc.Crlf);

        doc.Write("# Title\n\nChanged\n"); // the editor always hands over \n
        Assert.Equal("# Title\r\n\r\nChanged\r\n", File.ReadAllText(doc.Path!));
    }

    [Fact]
    public void Keeps_lf_line_endings_when_saving()
    {
        using var doc = Open("lf.md", Encoding.UTF8.GetBytes("a\nb\n"));
        doc.Read();
        Assert.False(doc.Crlf);

        doc.Write("a\r\nc\r\n");
        Assert.Equal("a\nc\n", File.ReadAllText(doc.Path!));
    }

    [Fact]
    public void Keeps_a_utf8_byte_order_mark_only_if_the_file_had_one()
    {
        using var withBom = Open("bom.md", [0xEF, 0xBB, 0xBF, (byte)'x']);
        withBom.Read();
        withBom.Write("y");
        Assert.Equal(new byte[] { 0xEF, 0xBB, 0xBF, (byte)'y' }, File.ReadAllBytes(withBom.Path!));

        using var withoutBom = Open("nobom.md", [(byte)'x']);
        withoutBom.Read();
        withoutBom.Write("y");
        Assert.Equal(new byte[] { (byte)'y' }, File.ReadAllBytes(withoutBom.Path!));
    }

    [Fact]
    public void Remembers_what_it_wrote_so_its_own_save_is_not_seen_as_an_outside_change()
    {
        using var doc = Open("own.md", Encoding.UTF8.GetBytes("old"));
        doc.Read();
        doc.Write("new");
        Assert.Equal("new", doc.DiskText);
        Assert.Equal(doc.DiskText, doc.Read());
        Assert.False(doc.Dirty);
    }

    [Fact]
    public void A_new_untitled_document_has_no_path_and_cannot_be_written()
    {
        using var doc = new Doc(7, null, _owner, _ => { }, "Untitled-3");
        Assert.Null(doc.Path);
        Assert.Equal("Untitled-3", doc.Name);
        Assert.Null(doc.Read());
        Assert.Throws<InvalidOperationException>(() => doc.Write("x"));
    }

    [Fact]
    public void Reading_a_missing_file_returns_null()
    {
        using var doc = new Doc(1, Path.Combine(_dir, "missing.md"), _owner, _ => { });
        Assert.Null(doc.Read());
    }
}
