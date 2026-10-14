using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Reflection;
using System.Text.Json;
using Microsoft.Win32;

namespace MarkdownViewer;

/// <summary>
/// A release from GitHub. <see cref="MsiUrl"/> is set only when the release also provides the
/// installer's SHA-256 (asset digest or a "-setup.msi.sha256" file), so it can be verified.
/// </summary>
sealed record UpdateInfo(Version Version, string Tag, string PageUrl, string? MsiUrl, string? MsiName,
    string? Sha256, string? Sha256Url);

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

        string? msiUrl = null, msiName = null, sha256 = null, sha256Url = null;
        var files = new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);
        if (root.TryGetProperty("assets", out var assets))
            foreach (var asset in assets.EnumerateArray())
                files[asset.GetProperty("name").GetString() ?? ""] = asset;

        if (files.Keys.FirstOrDefault(n => n.EndsWith("-setup.msi", StringComparison.OrdinalIgnoreCase)) is { } msi)
        {
            msiName = Path.GetFileName(msi);
            msiUrl = files[msi].GetProperty("browser_download_url").GetString();
            // GitHub reports "sha256:<hex>" for uploaded assets; our own .sha256 file is the fallback.
            if (files[msi].TryGetProperty("digest", out var digest) && digest.GetString() is { } d &&
                d.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase))
                sha256 = d[7..];
            if (files.TryGetValue(msi + ".sha256", out var shaAsset))
                sha256Url = shaAsset.GetProperty("browser_download_url").GetString();
            if (sha256 == null && sha256Url == null) msiUrl = null; // can't verify it: offer the download page instead
        }

        var page = root.TryGetProperty("html_url", out var html) ? html.GetString() : null;
        return new UpdateInfo(version, tag, page ?? "https://github.com/aljazmernik/markdown-viewer/releases",
            msiUrl, msiName, sha256, sha256Url);
    }

    /// <summary>
    /// Downloads the release's installer to the temp folder (reporting progress in percent) and
    /// verifies it; throws if the checksum or the publisher signature doesn't match.
    /// </summary>
    public static async Task<string> DownloadInstallerAsync(UpdateInfo update, IProgress<int> progress)
    {
        var expected = update.Sha256 ?? await GetPublishedChecksumAsync(update.Sha256Url!);
        var target = await DownloadAsync(update, progress);
        try
        {
            string actual;
            await using (var file = File.OpenRead(target))
                actual = Convert.ToHexString(await System.Security.Cryptography.SHA256.HashDataAsync(file));
            if (!actual.Equals(expected, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("The downloaded installer doesn't match the release's checksum.");
            Authenticode.RequireSamePublisher(target);
            return target;
        }
        catch
        {
            File.Delete(target);
            throw;
        }
    }

    static async Task<string> GetPublishedChecksumAsync(string url)
    {
        var text = await Http.GetStringAsync(url); // "<hex>  <file name>"
        var hex = text.Trim().Split(' ', '\t')[0];
        if (hex.Length != 64) throw new InvalidDataException("The release's checksum file is not valid.");
        return hex;
    }

    static async Task<string> DownloadAsync(UpdateInfo update, IProgress<int> progress)
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
