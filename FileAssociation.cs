using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace MarkdownViewer;

/// <summary>
/// Per-user (HKCU, no admin) registration of .md/.markdown files: opens them with this
/// exe and gives them the Markdown document icon. Run with --register / --unregister.
/// </summary>
static class FileAssociation
{
    const string ProgId = "MarkdownViewer.Markdown";
    static readonly string[] Extensions = [".md", ".markdown"];

    static readonly string IconPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MarkdownViewer", "file.ico");

    /// <summary>Registry location under HKCU; tests point this at a scratch key.</summary>
    internal static string ClassesKey { get; set; } = @"Software\Classes";

    public static void Register() => Register(Environment.ProcessPath!);

    internal static void Register(string exe)
    {
        // The exe is a single file, so the document icon is written next to the app data.
        Directory.CreateDirectory(Path.GetDirectoryName(IconPath)!);
        using (var res = typeof(FileAssociation).Assembly.GetManifestResourceStream("file.ico")!)
        using (var file = File.Create(IconPath))
            res.CopyTo(file);

        using var classes = Registry.CurrentUser.CreateSubKey(ClassesKey);
        using (var prog = classes.CreateSubKey(ProgId))
        {
            prog.SetValue("", "Markdown Document");
            using (var icon = prog.CreateSubKey("DefaultIcon")) icon.SetValue("", $"{IconPath},0");
            using (var cmd = prog.CreateSubKey(@"shell\open\command")) cmd.SetValue("", Command(exe));

            foreach (var ext in Extensions)
            {
                using var key = classes.CreateSubKey(ext);
                // Remember the previous handler so --unregister can put it back.
                if (key.GetValue("") is string previous && previous != ProgId)
                    prog.SetValue("Previous" + ext, previous);
                key.SetValue("", ProgId);
                using var openWith = key.CreateSubKey("OpenWithProgids");
                openWith.SetValue(ProgId, Array.Empty<byte>(), RegistryValueKind.None);
            }
        }
        NotifyShell();
    }

    public static bool Unregister(bool onlyIfRegisteredHere) => Unregister(Environment.ProcessPath!, onlyIfRegisteredHere);

    /// <summary>
    /// Removes the registration and restores the previous default app. With
    /// <paramref name="onlyIfRegisteredHere"/>, does nothing (returns false) unless the registration
    /// opens files with <paramref name="exe"/>; the installer uses this on uninstall so it never
    /// removes a registration made by another copy of the app.
    /// </summary>
    internal static bool Unregister(string exe, bool onlyIfRegisteredHere)
    {
        using var classes = Registry.CurrentUser.CreateSubKey(ClassesKey);
        using (var prog = classes.OpenSubKey(ProgId))
        {
            if (prog == null) return false;
            if (onlyIfRegisteredHere)
            {
                using var cmd = prog.OpenSubKey(@"shell\open\command");
                if (!string.Equals(cmd?.GetValue("") as string, Command(exe), StringComparison.OrdinalIgnoreCase))
                    return false;
            }

            foreach (var ext in Extensions)
            {
                using var key = classes.OpenSubKey(ext, writable: true);
                if (key == null) continue;
                if (key.GetValue("") as string == ProgId)
                {
                    if (prog.GetValue("Previous" + ext) is string previous) key.SetValue("", previous);
                    else key.DeleteValue("", throwOnMissingValue: false);
                }
                using var openWith = key.OpenSubKey("OpenWithProgids", writable: true);
                openWith?.DeleteValue(ProgId, throwOnMissingValue: false);
            }
        }
        classes.DeleteSubKeyTree(ProgId, throwOnMissingSubKey: false);
        if (ClassesKey == @"Software\Classes") try { File.Delete(IconPath); } catch { }
        NotifyShell();
        return true;
    }

    static string Command(string exe) => $"\"{exe}\" \"%1\"";

    static void NotifyShell() => SHChangeNotify(0x08000000 /* SHCNE_ASSOCCHANGED */, 0, IntPtr.Zero, IntPtr.Zero);

    [DllImport("shell32.dll")]
    static extern void SHChangeNotify(int eventId, uint flags, IntPtr item1, IntPtr item2);
}
