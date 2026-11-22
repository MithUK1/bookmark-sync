using System;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;

namespace BookmarkSync
{
    /// <summary>The few Windows calls that let a second start bring the running window to the front.</summary>
    internal static class Native
    {
        /// <summary>Lets any process take the foreground, so the window shown by the running copy is not left behind.</summary>
        public static void AllowAnyForeground()
        {
            AllowSetForegroundWindow(-1);
        }

        [DllImport("user32.dll")]
        private static extern bool AllowSetForegroundWindow(int processId);

        [DllImport("user32.dll")]
        public static extern bool SetForegroundWindow(IntPtr handle);
    }

    /// <summary>
    /// One program, several roles chosen by how it is started: the browsers' helper (a browser passes
    /// its extension address or the helper description file), --show and --tray for the window and
    /// tray icon, and --uninstall.
    /// </summary>
    public static class Program
    {
        [STAThread]
        public static int Main(string[] args)
        {
            string first = args.Length > 0 ? args[0] : "";

            // A browser starts the helper with the extension's address (Chrome, Edge) or the
            // description file and extension id (Firefox).
            if (first == "--host" || first.StartsWith("chrome-extension://", StringComparison.OrdinalIgnoreCase) || first.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
            {
                return RunHelper();
            }

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            if (first == "--uninstall")
            {
                Installer.Uninstall();
                return 0;
            }

            try
            {
                // Started from anywhere but the installed place (a download, the source folder),
                // install or upgrade and hand over to the installed copy.
                if (!Installer.RunningInstalledCopy)
                {
                    string note;
                    Installer.InstallAndHandOver(false, out note);
                    return 0;
                }
                return RunTray(first == "--tray");
            }
            catch (Exception ex)
            {
                Env.Log("Start-up failed: " + ex);
                MessageBox.Show("Bookmark Sync could not start.\r\n\r\n" + ex.Message, Env.AppTitle, MessageBoxButtons.OK, MessageBoxIcon.Error);
                return 1;
            }
        }

        /// <summary>Asks the running tray copy to show its window. Returns false when none is running.</summary>
        public static bool SignalRunningCopy()
        {
            try
            {
                Native.AllowAnyForeground();
                using (EventWaitHandle show = EventWaitHandle.OpenExisting("BookmarkSyncShow"))
                {
                    show.Set();
                }
                return true;
            }
            catch (WaitHandleCannotBeOpenedException)
            {
                return false;
            }
        }

        private static int RunHelper()
        {
            try
            {
                Host.Run();
                return 0;
            }
            catch (Exception ex)
            {
                Env.Log("Helper stopped: " + ex.Message);
                return 1;
            }
        }

        private static int RunTray(bool hidden)
        {
            bool created;
            using (var mutex = new Mutex(true, "BookmarkSyncApp", out created))
            {
                if (!created)
                {
                    // Already running: ask it to show its window.
                    SignalRunningCopy();
                    return 0;
                }
                Installer.CleanOldCopies();
                using (var app = new TrayApp(!hidden))
                {
                    Application.Run(app);
                }
                GC.KeepAlive(mutex);
            }
            return 0;
        }
    }
}
