using MarkdownViewer;
using Microsoft.Win32;
using Xunit;

// Uses a scratch key under HKCU instead of the real Software\Classes.
[Collection("registry")]
public sealed class FileAssociationTests : IDisposable
{
    readonly string _key = $@"Software\MarkdownViewerTests\{Guid.NewGuid():N}\Classes";
    readonly string _original = FileAssociation.ClassesKey;

    public FileAssociationTests()
    {
        FileAssociation.ClassesKey = _key;
        using var md = Registry.CurrentUser.CreateSubKey(_key + @"\.md");
        md.SetValue("", "previous_editor"); // the app that opened .md files before
    }

    public void Dispose()
    {
        FileAssociation.ClassesKey = _original;
        Registry.CurrentUser.DeleteSubKeyTree(_key[..(_key.LastIndexOf('\\'))], throwOnMissingSubKey: false);
        using var parent = Registry.CurrentUser.OpenSubKey(@"Software\MarkdownViewerTests");
        if (parent?.SubKeyCount == 0) Registry.CurrentUser.DeleteSubKey(@"Software\MarkdownViewerTests", false);
    }

    string? Default(string ext) => Registry.CurrentUser.OpenSubKey($@"{_key}\{ext}")?.GetValue("") as string;
    string? Command() => Registry.CurrentUser.OpenSubKey($@"{_key}\MarkdownViewer.Markdown\shell\open\command")?.GetValue("") as string;

    [Fact]
    public void Register_makes_the_app_the_default_for_md_files()
    {
        FileAssociation.Register(@"C:\Apps\MarkdownViewer.exe");
        Assert.Equal("MarkdownViewer.Markdown", Default(".md"));
        Assert.Equal("MarkdownViewer.Markdown", Default(".markdown"));
        Assert.Equal("\"C:\\Apps\\MarkdownViewer.exe\" \"%1\"", Command());
    }

    [Fact]
    public void Unregister_restores_the_previous_default_app()
    {
        FileAssociation.Register(@"C:\Apps\MarkdownViewer.exe");
        Assert.True(FileAssociation.Unregister(@"C:\Apps\MarkdownViewer.exe", onlyIfRegisteredHere: false));
        Assert.Equal("previous_editor", Default(".md"));
        Assert.Null(Command());
    }

    [Fact]
    public void Uninstall_leaves_a_registration_made_by_another_copy_alone()
    {
        FileAssociation.Register(@"C:\Dev\publish\MarkdownViewer.exe");
        var removed = FileAssociation.Unregister(@"C:\Users\x\AppData\Local\Programs\Markdown Viewer\MarkdownViewer.exe",
            onlyIfRegisteredHere: true);
        Assert.False(removed);
        Assert.Equal("MarkdownViewer.Markdown", Default(".md"));
        Assert.Equal("\"C:\\Dev\\publish\\MarkdownViewer.exe\" \"%1\"", Command());
    }

    [Fact]
    public void Uninstall_removes_a_registration_pointing_at_the_installed_copy()
    {
        const string installed = @"C:\Users\x\AppData\Local\Programs\Markdown Viewer\MarkdownViewer.exe";
        FileAssociation.Register(installed);
        Assert.True(FileAssociation.Unregister(installed, onlyIfRegisteredHere: true));
        Assert.Equal("previous_editor", Default(".md"));
    }

    [Fact]
    public void Unregister_without_a_registration_does_nothing()
    {
        Assert.False(FileAssociation.Unregister(@"C:\Apps\MarkdownViewer.exe", onlyIfRegisteredHere: false));
        Assert.Equal("previous_editor", Default(".md"));
    }
}
