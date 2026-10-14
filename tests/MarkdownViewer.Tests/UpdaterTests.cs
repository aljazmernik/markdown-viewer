using MarkdownViewer;
using Xunit;

public class UpdaterTests
{
    const string Hash = "0004de2320c385bbde5443c2b95a1996bb2f1d21d51b0804d8fa830366b11aa6";

    static string Release(string tag, string assets) =>
        $$"""{ "tag_name": "{{tag}}", "html_url": "https://example.test/release", "assets": [ {{assets}} ] }""";

    static string Asset(string name, string? digest = null) =>
        $$"""{ "name": "{{name}}", "browser_download_url": "https://example.test/{{name}}"{{(digest != null ? $", \"digest\": \"{digest}\"" : "")}} }""";

    [Fact]
    public void Reads_the_version_from_the_tag()
    {
        var info = Updater.ParseRelease(Release("v1.4.2", ""))!;
        Assert.Equal(new Version(1, 4, 2), info.Version);
        Assert.Equal("v1.4.2", info.Tag);
        Assert.Equal("https://example.test/release", info.PageUrl);
    }

    [Fact]
    public void Ignores_tags_that_are_not_versions()
    {
        Assert.Null(Updater.ParseRelease(Release("nightly", "")));
    }

    [Fact]
    public void Uses_githubs_asset_digest_as_the_checksum()
    {
        var info = Updater.ParseRelease(Release("v2.0.0", Asset("MarkdownViewer-2.0.0-setup.msi", "sha256:" + Hash)))!;
        Assert.Equal("https://example.test/MarkdownViewer-2.0.0-setup.msi", info.MsiUrl);
        Assert.Equal("MarkdownViewer-2.0.0-setup.msi", info.MsiName);
        Assert.Equal(Hash, info.Sha256);
    }

    [Fact]
    public void Falls_back_to_the_published_checksum_file()
    {
        var info = Updater.ParseRelease(Release("v2.0.0",
            Asset("MarkdownViewer-2.0.0-setup.msi") + "," + Asset("MarkdownViewer-2.0.0-setup.msi.sha256")))!;
        Assert.NotNull(info.MsiUrl);
        Assert.Null(info.Sha256);
        Assert.Equal("https://example.test/MarkdownViewer-2.0.0-setup.msi.sha256", info.Sha256Url);
    }

    [Fact]
    public void Does_not_offer_automatic_install_without_any_checksum()
    {
        var info = Updater.ParseRelease(Release("v2.0.0", Asset("MarkdownViewer-2.0.0-setup.msi")))!;
        Assert.Null(info.MsiUrl); // the app offers the download page instead
    }

    [Fact]
    public void Ignores_assets_that_are_not_the_installer()
    {
        var info = Updater.ParseRelease(Release("v2.0.0", Asset("MarkdownViewer-2.0.0.exe", "sha256:" + Hash)))!;
        Assert.Null(info.MsiUrl);
    }

    [Theory]
    [InlineData(Hash + "  MarkdownViewer-2.0.0-setup.msi\n")]
    [InlineData(Hash + "\n")]
    [InlineData("  " + Hash + "\tfile.msi")]
    public void Parses_checksum_files(string text)
    {
        Assert.Equal(Hash, Updater.ParseChecksum(text));
    }

    [Theory]
    [InlineData("")]
    [InlineData("abc123  file.msi")]
    [InlineData("zz04de2320c385bbde5443c2b95a1996bb2f1d21d51b0804d8fa830366b11aa6  file.msi")]
    public void Rejects_invalid_checksum_files(string text)
    {
        Assert.Throws<InvalidDataException>(() => Updater.ParseChecksum(text));
    }
}
