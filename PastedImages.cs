using System.Text.RegularExpressions;

namespace MarkdownViewer;

/// <summary>Images pasted or dropped into the editor are saved in an "images" folder next to the
/// document and linked with a relative path.</summary>
static class PastedImages
{
    public const string Folder = "images";

    public sealed record CopyResult(int Copied, IReadOnlyList<string> Outside);

    /// <summary>After "Save as" into another folder: copies the images the document links with
    /// relative paths (such as images/x.png) so the links keep working there. Files already at the
    /// destination are left alone. Links that would land outside the new folder (../x.png) aren't
    /// copied and are returned in <see cref="CopyResult.Outside"/>.</summary>
    public static CopyResult CopyLinked(string markdown, string fromDir, string toDir)
    {
        int copied = 0;
        var outside = new List<string>();
        var root = Path.GetFullPath(toDir).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        foreach (var link in MarkdownRenderer.RelativeImagePaths(markdown).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            string source, target;
            try { source = Path.GetFullPath(Path.Combine(fromDir, link)); target = Path.GetFullPath(Path.Combine(toDir, link)); }
            catch (ArgumentException) { continue; }   // not a valid path
            if (!File.Exists(source) || File.Exists(target)) continue; // missing anyway, or already there
            if (!target.StartsWith(root, StringComparison.OrdinalIgnoreCase)) { outside.Add(link); continue; }
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(source, target);
            copied++;
        }
        return new CopyResult(copied, outside);
    }

    public static string? Extension(string? mime) => mime switch
    {
        "image/png" => ".png",
        "image/jpeg" => ".jpg",
        "image/gif" => ".gif",
        "image/webp" => ".webp",
        "image/bmp" => ".bmp",
        "image/svg+xml" => ".svg",
        "image/avif" => ".avif",
        _ => null,
    };

    /// <summary>Saves the image and returns the link for the markdown, relative to the document
    /// (e.g. "images/notes-20261024-153012.png"), or null for an unsupported type.</summary>
    public static string? Save(string docPath, string? originalName, string? mime, byte[] data, DateTime now)
    {
        if (Extension(mime) is not { } ext) return null;
        var dir = Path.Combine(Path.GetDirectoryName(docPath)!, Folder);
        Directory.CreateDirectory(dir);
        var name = UniqueName(dir, BaseName(docPath, originalName, now), ext);
        File.WriteAllBytes(Path.Combine(dir, name), data);
        return $"{Folder}/{name}";
    }

    /// <summary>A dropped file keeps its own name; a pasted screenshot (which the browser calls
    /// "image.png") is named after the document and the time.</summary>
    internal static string BaseName(string docPath, string? originalName, DateTime now)
    {
        var stem = Clean(Path.GetFileNameWithoutExtension(originalName ?? ""));
        if (stem is "" or "image" or "blob")
            stem = $"{Clean(Path.GetFileNameWithoutExtension(docPath))}-{now:yyyyMMdd-HHmmss}".TrimStart('-');
        return stem;
    }

    /// <summary>Letters, digits, dot, dash and underscore only, so the link needs no escaping.</summary>
    static string Clean(string s) =>
        Regex.Replace(Regex.Replace(s.Trim(), @"[^\p{L}\p{N}._-]+", "-"), "-{2,}", "-").Trim('-', '.');

    static string UniqueName(string dir, string stem, string ext)
    {
        var name = stem + ext;
        for (int i = 2; File.Exists(Path.Combine(dir, name)); i++) name = $"{stem}-{i}{ext}";
        return name;
    }
}
