using Microsoft.Win32;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text;

namespace MarkdownViewer;

static class Program
{
    // One window per user: launching again (e.g. double-clicking another .md file)
    // hands the paths to the running instance, which opens them as tabs.
    static readonly string InstanceName = $"MarkdownViewer.{Environment.UserName}";

    [STAThread]
    static void Main(string[] args)
    {
        // Uninstall entry in Settings > Apps (installed copy): undo --register if it points here,
        // then run the MSI uninstall.
        if (args.Length > 0 && args[0] == "--uninstall")
        {
            Uninstall(quiet: args.Contains("--quiet"));
            return;
        }

        // --register / --unregister [--quiet] [--if-registered-here]
        if (args.Length > 0 && args[0] is "--register" or "--unregister")
        {
            var register = args[0] == "--register";
            var quiet = args.Contains("--quiet");
            try
            {
                if (register) FileAssociation.Register();
                else FileAssociation.Unregister(onlyIfRegisteredHere: args.Contains("--if-registered-here"));
                if (!quiet)
                    MessageBox.Show(register
                        ? "Markdown Viewer now opens .md and .markdown files."
                        : "Markdown Viewer file association removed.", "Markdown Viewer");
            }
            catch (Exception ex)
            {
                if (!quiet) MessageBox.Show(ex.Message, "Markdown Viewer", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            return;
        }

        var paths = args.Select(Path.GetFullPath).ToArray();

        using var mutex = new Mutex(true, InstanceName, out bool firstInstance);
        if (!firstInstance && SendToRunningInstance(paths)) return;

        ApplicationConfiguration.Initialize();
        var form = new MainForm(paths);
        if (firstInstance) StartListening(form);
        Application.Run(form);
    }

    static void Uninstall(bool quiet)
    {
        const string entry = @"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Uninstall\MarkdownViewer";
        if (Registry.GetValue(entry, "ProductCode", null) is not string productCode)
        {
            if (!quiet) MessageBox.Show("Markdown Viewer doesn't seem to be installed.", "Markdown Viewer");
            return;
        }
        // Runs here, in the user's own session: processes started by Windows Installer see a
        // stale view of the user's file-type registry and can't do this.
        try { FileAssociation.Unregister(onlyIfRegisteredHere: true); } catch { /* still uninstall */ }

        // msiexec shows its usual "Are you sure?" prompt and progress (none with --quiet).
        // This process exits right away, so its exe can be removed.
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("msiexec.exe",
            $"/x {productCode}{(quiet ? " /qn" : "")}") { UseShellExecute = false });
    }

    static bool SendToRunningInstance(string[] paths)
    {
        try
        {
            using var pipe = new NamedPipeClientStream(".", InstanceName, PipeDirection.Out, PipeOptions.CurrentUserOnly);
            pipe.Connect(2000);
            AllowSetForegroundWindow(-1 /* ASFW_ANY */); // let the running window come to the front
            var data = Encoding.UTF8.GetBytes(string.Join('\n', paths));
            pipe.Write(data);
            return true;
        }
        catch
        {
            return false; // not responding: just start a separate window
        }
    }

    static void StartListening(MainForm form)
    {
        var thread = new Thread(() =>
        {
            while (true)
            {
                try
                {
                    using var pipe = new NamedPipeServerStream(InstanceName, PipeDirection.In, 1,
                        PipeTransmissionMode.Byte, PipeOptions.CurrentUserOnly);
                    pipe.WaitForConnection();
                    using var reader = new StreamReader(pipe, Encoding.UTF8);
                    var paths = reader.ReadToEnd().Split('\n', StringSplitOptions.RemoveEmptyEntries);
                    if (form.IsHandleCreated) form.BeginInvoke(() => form.OpenFromOtherInstance(paths));
                }
                catch
                {
                    Thread.Sleep(200);
                }
            }
        }) { IsBackground = true, Name = "InstancePipe" };
        thread.Start();
    }

    [DllImport("user32.dll")]
    static extern bool AllowSetForegroundWindow(int processId);
}
