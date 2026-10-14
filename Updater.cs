using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Reflection;
using System.Text.Json;
using Microsoft.Win32;

namespace MarkdownViewer;

/// <summary>A release newer than this build, from GitHub.</summary>
sealed record UpdateInfo(Version Version, string Tag, string PageUrl, string? MsiUrl, string? MsiName);

/// <summary>Checks GitHub Releases for a newer version and installs it via the MSI.</summary>
static class Updater
{
    const string LatestReleaseApi = "https://api.github.com/repos/aljazmernik/markdown-viewer/releases/latest";

    // For testing against a local feed: MARKDOWNVIEWER_UPDATE_FEED=http://localhost:8123/latest
    static string FeedUrl => Environment.GetEnvironmentVariable("MARKDOWNVIEWER_UPDATE_FEED") ?? LatestReleaseApi;

    public static readonly string CurrentText = typeof(Updater).Assembly
        .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0.0.0";

    public static readonly Version Current = Version.TryParse(CurrentText, out var v) ? v : new Version(0, 0, 0);

    static readonly HttpClient Http = CreateClient();

    static HttpClient CreateClient()
    {
        var http = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("MarkdownViewer", CurrentText));
        http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        return http;
    }

    /// <summary>True when running from the folder the MSI installed to (so the MSI can update it).</summary>
    public static bool IsInstalled
    {
        get
        {
            var installDir = Registry.GetValue(@"HKEY_CURRENT_USER\Software\Markdown Viewer", "InstallDir", null) as string;
            var exeDir = Path.GetDirectoryName(Environment.ProcessPath);
            return installDir != null && exeDir != null &&
                   string.Equals(Path.TrimEndingDirectorySeparator(installDir), Path.TrimEndingDirectorySeparator(exeDir),
                       StringComparison.OrdinalIgnoreCase);
        }
    }

    /// <summary>The latest published release, or null if there is none. Throws on network errors.</summary>
    public static async Task<UpdateInfo?> GetLatestAsync()
    {
        using var response = await Http.GetAsync(FeedUrl);
        if (response.StatusCode == HttpStatusCode.NotFound) return null; // no releases yet
        response.EnsureSuccessStatusCode();

        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = json.RootElement;
        var tag = root.GetProperty("tag_name").GetString() ?? "";
        if (!Version.TryParse(tag.TrimStart('v', 'V'), out var version)) return null;

        string? msiUrl = null, msiName = null;
        if (root.TryGetProperty("assets", out var assets))
            foreach (var asset in assets.EnumerateArray())
            {
                var name = asset.GetProperty("name").GetString() ?? "";
                if (!name.EndsWith("-setup.msi", StringComparison.OrdinalIgnoreCase)) continue;
                msiName = Path.GetFileName(name);
                msiUrl = asset.GetProperty("browser_download_url").GetString();
                break;
            }

        var page = root.TryGetProperty("html_url", out var html) ? html.GetString() : null;
        return new UpdateInfo(version, tag, page ?? "https://github.com/aljazmernik/markdown-viewer/releases", msiUrl, msiName);
    }

    /// <summary>Downloads the release's installer to the temp folder, reporting progress in percent.</summary>
    public static async Task<string> DownloadInstallerAsync(UpdateInfo update, IProgress<int> progress)
    {
        var target = Path.Combine(Path.GetTempPath(), update.MsiName!);
        using var response = await Http.GetAsync(update.MsiUrl, HttpCompletionOption.ResponseHeadersRead);
        response.EnsureSuccessStatusCode();
        var total = response.Content.Headers.ContentLength;

        await using (var source = await response.Content.ReadAsStreamAsync())
        await using (var file = File.Create(target))
        {
            var buffer = new byte[81920];
            long done = 0;
            int read;
            while ((read = await source.ReadAsync(buffer)) > 0)
            {
                await file.WriteAsync(buffer.AsMemory(0, read));
                done += read;
                if (total > 0) progress.Report((int)(done * 100 / total.Value));
            }
        }
        return target;
    }

    /// <summary>
    /// Runs the installer once this process has exited, then starts the (updated) app again.
    /// The caller closes the app right after calling this.
    /// </summary>
    public static void InstallAndRestart(string msiPath)
    {
        var exe = Environment.ProcessPath!;
        var script = $"/c ping -n 3 127.0.0.1 >nul & start \"\" /wait msiexec /i \"{msiPath}\" /qb & start \"\" \"{exe}\"";
        Process.Start(new ProcessStartInfo("cmd.exe", script) { CreateNoWindow = true, UseShellExecute = false });
    }
}
