using System.Text;

namespace MarkdownViewer;

/// <summary>An open file (one tab). <see cref="Path"/> is null for a new file that was never saved.</summary>
sealed class Doc : IDisposable
{
    public int Id { get; }
    public string? Path { get; private set; }
    public string Name => Path != null ? System.IO.Path.GetFileName(Path) : _untitledName;
    public string? DiskText { get; set; }   // content as last read or written by us
    public bool Dirty { get; set; }
    public bool Crlf { get; private set; }  // preserved when saving
    public bool Bom { get; private set; }

    readonly string _untitledName;
    readonly Control _owner;
    FileSystemWatcher? _watcher;
    readonly System.Windows.Forms.Timer _debounce = new() { Interval = 120 };

    public Doc(int id, string? path, Control owner, Action<Doc> onChanged, string untitledName = "Untitled")
    {
        Id = id;
        _owner = owner;
        _untitledName = untitledName;
        _debounce.Tick += (_, _) => { _debounce.Stop(); onChanged(this); };
        if (path != null) SetPath(path);
    }

    /// <summary>Points the document at a (new) file and watches it for outside changes.</summary>
    public void SetPath(string path)
    {
        Path = path;
        _watcher?.Dispose();
        _watcher = null;

        var dir = System.IO.Path.GetDirectoryName(path);
        if (dir == null || !Directory.Exists(dir)) return;
        _watcher = new FileSystemWatcher(dir, System.IO.Path.GetFileName(path))
        {
            NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.FileName | NotifyFilters.CreationTime,
        };
        void Schedule()
        {
            if (_owner.IsDisposed) return;
            _owner.BeginInvoke(() => { _debounce.Stop(); _debounce.Start(); });
        }
        _watcher.Changed += (_, _) => Schedule();
        _watcher.Created += (_, _) => Schedule();
        _watcher.Renamed += (_, _) => Schedule(); // editors that save via temp file + rename
        _watcher.EnableRaisingEvents = true;
    }

    public string? Read()
    {
        if (Path == null) return null;
        for (int attempt = 0; attempt < 5; attempt++)
        {
            try
            {
                var bytes = File.ReadAllBytes(Path);
                Bom = bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF;
                using var reader = new StreamReader(new MemoryStream(bytes), Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
                var text = reader.ReadToEnd();
                Crlf = text.Contains("\r\n");
                return text;
            }
            catch (FileNotFoundException) { return null; }
            catch (DirectoryNotFoundException) { return null; }
            catch (IOException) { Thread.Sleep(40); } // file still being written
        }
        return null;
    }

    /// <summary>Writes markdown (with \n line endings) back in the file's original style.</summary>
    public void Write(string markdown)
    {
        if (Path == null) throw new InvalidOperationException("Document has no file yet.");
        var text = markdown.Replace("\r\n", "\n");
        if (Crlf) text = text.Replace("\n", "\r\n");
        DiskText = text; // set first so the watcher event for this write is ignored
        try { File.WriteAllText(Path, text, new UTF8Encoding(Bom)); }
        catch { DiskText = null; throw; }
        Dirty = false;
    }

    public void Dispose()
    {
        _watcher?.Dispose();
        _debounce.Dispose();
    }
}
