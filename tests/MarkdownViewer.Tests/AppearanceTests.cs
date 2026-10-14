using MarkdownViewer;
using Xunit;
using Appearance = MarkdownViewer.Appearance; // not System.Windows.Forms.Appearance

public class AppearanceTests
{
    [Fact]
    public void Reads_theme_name_and_base_from_the_header_comment()
    {
        var (name, dark) = Appearance.ParseHeader("/* Markdown Viewer theme | name: Forest Night | base: dark */\n:root{}", "file");
        Assert.Equal("Forest Night", name);
        Assert.True(dark);
    }

    [Fact]
    public void A_theme_without_a_header_is_light_and_named_after_its_file()
    {
        var (name, dark) = Appearance.ParseHeader(":root { --bg: #fff; }", "My theme 1");
        Assert.Equal("My theme 1", name);
        Assert.False(dark);
    }

    [Fact]
    public void Reads_css_variables()
    {
        var vars = Appearance.ReadVariables(":root {\n  --bg: #2e3440; --fg: #d8dee9;\n  --accent:#5e81ac }");
        Assert.Equal("#2e3440", vars["--bg"]);
        Assert.Equal("#d8dee9", vars["--fg"]);
        Assert.Equal("#5e81ac", vars["--accent"]);
    }

    [Fact]
    public void All_built_in_themes_load_with_their_colors()
    {
        var themes = Appearance.LoadThemes().Where(t => !t.Custom).ToList();
        Assert.Equal(["system", "light", "dark", "sepia", "solarized-light", "nord", "solarized-dark"], themes.Select(t => t.Id));
        Assert.True(themes.Single(t => t.Id == "nord").Dark);
        Assert.False(themes.Single(t => t.Id == "sepia").Dark);
        Assert.Equal("#2e3440", themes.Single(t => t.Id == "nord").Swatch(false)["bg"]);
    }

    static Theme Nord() => Appearance.LoadThemes().Single(t => t.Id == "nord");

    [Fact]
    public void Stylesheet_has_the_theme_colors_and_the_text_settings()
    {
        var css = Appearance.BuildCss(Nord(), new AppearanceSettings { FontSize = 18, Width = "full", Font = "serif" });
        Assert.Contains("--bg: #2e3440", css);
        Assert.Contains("--font-size: 18px", css);
        Assert.Contains("--content-width: none", css);
        Assert.Contains("Georgia", css);
        Assert.DoesNotContain("background:", css.Split("/* background */").ElementAtOrDefault(1) ?? "");
    }

    [Fact]
    public void Stylesheet_adds_the_background_image_with_its_options()
    {
        var image = Path.Combine(Path.GetTempPath(), $"mv-bg-{Guid.NewGuid():N}.png");
        File.WriteAllBytes(image, [0x89, 0x50, 0x4E, 0x47]);
        try
        {
            var css = Appearance.BuildCss(Nord(), new AppearanceSettings
            {
                Background = image, BackgroundFit = "tile", BackgroundOpacity = 40, BackgroundBlur = 5, Paper = true,
            });
            Assert.Contains("/* background */", css);
            Assert.Contains(Path.GetFileName(image), css);
            Assert.Contains("repeat", css);
            Assert.Contains("opacity: 0.4", css);
            Assert.Contains("blur(5px)", css);
            Assert.Contains("#preview .markdown-body", css); // the "paper" card
        }
        finally { File.Delete(image); }
    }

    [Fact]
    public void A_missing_background_image_is_ignored()
    {
        var css = Appearance.BuildCss(Nord(), new AppearanceSettings { Background = @"C:\nope\missing.png" });
        Assert.DoesNotContain("/* background */", css);
    }

    [Fact]
    public void Settings_from_a_hand_edited_file_are_clamped()
    {
        var look = new AppearanceSettings { FontSize = 200, BackgroundOpacity = -5, BackgroundBlur = 99, Width = "huge", Font = "comic" }.Normalized();
        Assert.Equal(26, look.FontSize);
        Assert.Equal(5, look.BackgroundOpacity);
        Assert.Equal(30, look.BackgroundBlur);
        Assert.Equal("normal", look.Width);
        Assert.Equal("sans", look.Font);
    }
}
