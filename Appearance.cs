using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace MarkdownViewer;

/// <summary>What the user picked in the Appearance panel (saved in settings.json).</summary>
sealed class AppearanceSettings
{
    public string Theme { get; set; } = "system";
    public string Font { get; set; } = "sans";        // sans | serif
    public int FontSize { get; set; } = 16;           // px
    public string Width { get; set; } = "normal";     // narrow | normal | wide | full
    public string? Background { get; set; }           // image file (copied into the app's backgrounds folder)
    public string BackgroundFit { get; set; } = "cover"; // cover | contain | tile
    public int BackgroundOpacity { get; set; } = 60;  // %
    public int BackgroundBlur { get; set; }           // px
    public bool Paper { get; set; } = true;           // card behind the text when there is a background

    /// <summary>Clamps values from a hand-edited or older settings file.</summary>
    public AppearanceSettings Normalized()
    {
        FontSize = Math.Clamp(FontSize, 12, 26);
        BackgroundOpacity = Math.Clamp(BackgroundOpacity, 5, 100);
        BackgroundBlur = Math.Clamp(BackgroundBlur, 0, 30);
        if (Font is not ("sans" or "serif")) Font = "sans";
        if (Width is not ("narrow" or "normal" or "wide" or "full")) Width = "normal";
        if (BackgroundFit is not ("cover" or "contain" or "tile")) BackgroundFit = "cover";
        return this;
    }
}

/// <summary>A color theme: built in, or a .css file in the user's themes folder.</summary>
sealed record Theme(string Id, string Name, bool Dark, string Css, bool Custom, string? FilePath)
{
    /// <summary>Colors for the theme's preview card in the Appearance panel.</summary>
    public Dictionary<string, string> Swatch(bool windowsDark)
    {
        var vars = Appearance.ReadVariables(Css);
        var dark = Id == "system" ? windowsDark : Dark;
        string Get(string name, string light, string darkValue) => vars.GetValueOrDefault(name) ?? (dark ? darkValue : light);
        return new()
        {
            ["bg"] = Get("--bg", "#ffffff", "#0d1117"),
            ["fg"] = Get("--fg", "#1f2328", "#f0f6fc"),
            ["bar"] = Get("--bar", "#f6f8fa", "#151b23"),
            ["accent"] = Get("--accent", "#0969da", "#1f6feb"),
            ["border"] = Get("--border", "#d1d9e0", "#3d444d"),
        };
    }
}

static class Appearance
{
    static readonly string[] Starters = ["sepia", "solarized-light", "nord", "solarized-dark"];

    public static string ThemesDir { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MarkdownViewer", "themes");

    public static string BackgroundsDir { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MarkdownViewer", "backgrounds");

    /// <summary>Built-in themes followed by the user's own (sorted by name).</summary>
    public static List<Theme> LoadThemes()
    {
        var themes = new List<Theme>
        {
            new("system", "System", false, "", false, null),
            new("light", "Light", false, "", false, null),
            new("dark", "Dark", true, "", false, null),
        };
        foreach (var id in Starters)
        {
            var css = ReadResource($"web/themes/{id}.css");
            var (name, dark) = ParseHeader(css, id);
            themes.Add(new Theme(id, name, dark, css, false, null));
        }
        if (Directory.Exists(ThemesDir))
            foreach (var file in Directory.GetFiles(ThemesDir, "*.css").Order(StringComparer.OrdinalIgnoreCase))
            {
                string css;
                try { css = File.ReadAllText(file); } catch (IOException) { continue; } // being saved
                var (name, dark) = ParseHeader(css, Path.GetFileNameWithoutExtension(file));
                themes.Add(new Theme("custom:" + Path.GetFileName(file), name, dark, css, true, file));
            }
        return themes;
    }

    /// <summary>Reads "name: ..." and "base: light|dark" from the theme's first comment.</summary>
    internal static (string Name, bool Dark) ParseHeader(string css, string fallbackName)
    {
        var header = Regex.Match(css, @"/\*(.*?)\*/", RegexOptions.Singleline);
        var text = header.Success ? header.Groups[1].Value : "";
        var name = Regex.Match(text, @"name\s*:\s*([^|\r\n*]+)", RegexOptions.IgnoreCase);
        var baseMatch = Regex.Match(text, @"base\s*:\s*(light|dark)", RegexOptions.IgnoreCase);
        return (name.Success ? name.Groups[1].Value.Trim() : fallbackName,
                baseMatch.Success && baseMatch.Groups[1].Value.Equals("dark", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>CSS custom properties ("--name: value") declared anywhere in the theme.</summary>
    internal static Dictionary<string, string> ReadVariables(string css)
    {
        var vars = new Dictionary<string, string>();
        foreach (Match m in Regex.Matches(css, @"(--[\w-]+)\s*:\s*([^;}]+)"))
            vars[m.Groups[1].Value] = m.Groups[2].Value.Trim();
        return vars;
    }

    /// <summary>The stylesheet every page links to: theme colors, then text and background settings.</summary>
    internal static string BuildCss(Theme theme, AppearanceSettings look)
    {
        var css = new StringBuilder();
        css.Append("/* theme: ").Append(theme.Name).Append(" */\n").Append(theme.Css).Append('\n');

        var width = look.Width switch { "narrow" => "720px", "wide" => "1200px", "full" => "none", _ => "900px" };
        var font = look.Font == "serif"
            ? "Charter, \"Iowan Old Style\", Georgia, Cambria, \"Times New Roman\", serif"
            : "-apple-system, \"Segoe UI\", \"Noto Sans\", Helvetica, Arial, sans-serif";
        css.Append(CultureInfo.InvariantCulture,
            $":root {{ --font-size: {look.FontSize}px; --content-width: {width}; --font-body: {font}; }}\n");

        if (look.Background is { } image && File.Exists(image))
        {
            var url = "/" + new Uri(image).AbsolutePath.TrimStart('/'); // served by the app as /C:/...
            var layout = look.BackgroundFit switch
            {
                "contain" => "center / contain no-repeat",
                "tile" => "repeat",
                _ => "center / cover no-repeat",
            };
            css.Append(CultureInfo.InvariantCulture, $$"""
                /* background */
                #main { position: relative; isolation: isolate; overflow: hidden; }
                #main::before {
                  content: ""; position: absolute; inset: {{(look.BackgroundBlur > 0 ? -look.BackgroundBlur * 3 : 0)}}px;
                  z-index: -1; pointer-events: none;
                  background: url("{{url}}") {{layout}};
                  opacity: {{look.BackgroundOpacity / 100.0:0.##}};
                  filter: blur({{look.BackgroundBlur}}px);
                }

                """);
            if (look.Paper)
                css.Append("""
                    #preview .markdown-body {
                      background: color-mix(in srgb, var(--bg) 90%, transparent);
                      border-radius: 12px; margin-top: 28px; margin-bottom: 48px;
                      box-shadow: 0 8px 32px rgb(0 0 0 / 0.18);
                    }

                    """);
        }
        return css.ToString();
    }

    /// <summary>
    /// Creates a new theme file in the themes folder, starting from <paramref name="from"/>,
    /// with every color variable listed so it can be edited. Returns its path.
    /// </summary>
    public static string CreateCustomTheme(Theme from, bool windowsDark)
    {
        Directory.CreateDirectory(ThemesDir);
        var n = 1;
        string path;
        do path = Path.Combine(ThemesDir, $"My theme {n++}.css"); while (File.Exists(path));
        var name = Path.GetFileNameWithoutExtension(path);

        var dark = from.Id == "system" ? windowsDark : from.Dark;
        var swatch = from.Swatch(windowsDark);
        var vars = ReadVariables(from.Css);
        string V(string key, string light, string darkValue) => vars.GetValueOrDefault(key) ?? (dark ? darkValue : light);
        // Extra rules of the source theme (e.g. code colors), without its header and :root block.
        var extra = Regex.Replace(Regex.Replace(from.Css, @"^/\*.*?\*/\s*", "", RegexOptions.Singleline),
            @":root\s*\{[^}]*\}\s*", "").Trim();

        var css = $$"""
            /* Markdown Viewer theme | name: {{name}} | base: {{(dark ? "dark" : "light")}}

               Save this file and Markdown Viewer updates right away.
               "base" picks the light or dark style for the editor and code highlighting.
               Colors can be any CSS color. You can also add your own CSS rules below,
               for example: .markdown-body h1 { letter-spacing: -0.02em; }
            */
            :root {
              --bg: {{swatch["bg"]}};          /* page background */
              --fg: {{swatch["fg"]}};          /* text */
              --muted: {{V("--muted", "#59636e", "#9198a1")}};       /* secondary text */
              --bar: {{swatch["bar"]}};         /* toolbar, outline, status bar */
              --strip: {{V("--strip", "#e6eaef", "#010409")}};       /* tab strip */
              --border: {{swatch["border"]}};      /* lines and borders */
              --code-bg: {{V("--code-bg", "#f6f8fa", "#151b23")}};     /* code blocks, table stripes */
              --link: {{V("--link", "#0969da", "#4493f8")}};        /* links */
              --accent: {{swatch["accent"]}};      /* selected buttons, highlights */
              --on-accent: {{V("--on-accent", "#ffffff", "#ffffff")}};   /* text on accent color */
              --warn-bg: {{V("--warn-bg", "#fff8c5", "#3b2e00")}};     /* "file changed" banner */
              --warn-fg: {{V("--warn-fg", "#3b2e00", "#f0f6fc")}};
            }

            {{extra}}
            """;
        File.WriteAllText(path, css.Replace("\r\n", "\n").TrimEnd() + "\n");
        return path;
    }

    static string ReadResource(string name)
    {
        var assembly = typeof(Appearance).Assembly;
        var actual = assembly.GetManifestResourceNames().FirstOrDefault(n => n.Replace('\\', '/') == name)
            ?? throw new InvalidOperationException($"Missing resource {name}");
        using var stream = assembly.GetManifestResourceStream(actual)!;
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
