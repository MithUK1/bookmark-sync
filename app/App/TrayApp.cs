using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Threading;
using System.Windows.Forms;

namespace BookmarkSync
{
    /// <summary>
    /// The program while it runs: a tray icon with a right-click menu, the main window (made on first
    /// use), a minute timer for the midnight backups, and listeners for "show" and "exit" requests
    /// from a second start or from an upgrade.
    /// </summary>
    public sealed class TrayApp : ApplicationContext, IDisposable
    {
        private static readonly string[][] MasterChoices =
        {
            new[] { "off", "Off, merge all browsers" },
            new[] { "auto", "Automatic (first synced in priority order)" },
            new[] { "firefox", "Firefox" },
            new[] { "chrome", "Chrome" },
            new[] { "edge", "Edge" }
        };

        private readonly NotifyIcon icon = new NotifyIcon();
        private readonly ContextMenuStrip menu = new ContextMenuStrip();
        private readonly System.Windows.Forms.Timer clock = new System.Windows.Forms.Timer { Interval = 60000 };
        private readonly EventWaitHandle showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, "BookmarkSyncShow");
        private readonly EventWaitHandle exitEvent = new EventWaitHandle(false, EventResetMode.AutoReset, "BookmarkSyncExit");
        private readonly RegisteredWaitHandle showWait;
        private readonly RegisteredWaitHandle exitWait;
        private readonly Control marshal = new Control();
        private MainForm window;
        private string lastBackupDay = DateTime.Now.ToString("yyyyMMdd");

        /// <summary>The running instance, so the Settings window can ask it to exit.</summary>
        public static TrayApp Current { get; private set; }

        public bool Exiting { get; private set; }

        public TrayApp(bool showWindow)
        {
            Current = this;
            marshal.CreateControl();
            IntPtr force = marshal.Handle;

            icon.Icon = Theme.AppIcon;
            icon.Text = Env.AppTitle;
            icon.ContextMenuStrip = menu;
            icon.MouseClick += (s, e) => { if (e.Button == MouseButtons.Left) ShowWindow(); };
            menu.Opening += (s, e) => BuildMenu();
            icon.Visible = true;

            showWait = ThreadPool.RegisterWaitForSingleObject(showEvent, (state, timedOut) => Post(ShowWindow), null, -1, false);
            exitWait = ThreadPool.RegisterWaitForSingleObject(exitEvent, (state, timedOut) => Post(ExitApp), null, -1, false);

            clock.Tick += (s, e) => DailyBackups();
            clock.Start();

            string note = Installer.TakeStartupNote();
            if (note != null)
            {
                icon.ShowBalloonTip(6000, Env.AppTitle, "Bookmark Sync " + note + ".", ToolTipIcon.Info);
                showWindow = true;
            }
            if (showWindow) ShowWindow();
        }

        /// <summary>Runs an action on the interface thread.</summary>
        private void Post(Action action)
        {
            try
            {
                marshal.BeginInvoke(action);
            }
            catch (Exception ex)
            {
                Env.Log("Could not pass a request to the interface: " + ex.Message);
            }
        }

        public void ShowWindow()
        {
            if (window == null || window.IsDisposed) window = new MainForm(this);
            window.Show();
            if (window.WindowState == FormWindowState.Minimized) window.WindowState = FormWindowState.Normal;
            // A window shown by a request from another process can stay behind, so bring it forward every time.
            window.TopMost = true;
            window.Activate();
            Native.SetForegroundWindow(window.Handle);
            window.TopMost = false;
        }

        public void ExitApp()
        {
            Exiting = true;
            clock.Stop();
            icon.Visible = false;
            if (window != null && !window.IsDisposed) window.Close();
            ExitThread();
        }

        private void BuildMenu()
        {
            menu.Items.Clear();
            var open = new ToolStripMenuItem("Open Bookmark Sync") { Font = new Font(menu.Font, FontStyle.Bold) };
            open.Click += (s, e) => ShowWindow();
            menu.Items.Add(open);
            menu.Items.Add(new ToolStripSeparator());

            Config config = Config.Load();
            try
            {
                State state = State.Load();
                Status status = Status.Load();
                foreach (string browser in state.Browsers.Keys.OrderBy(k => k))
                {
                    string text = Env.BrowserName(browser) + ": " + (status.IsCurrent(browser) ? "up to date" : "changes waiting");
                    menu.Items.Add(new ToolStripMenuItem(text) { Enabled = false });
                }
                if (state.Browsers.Count == 0) menu.Items.Add(new ToolStripMenuItem("No browser has synced yet") { Enabled = false });
            }
            catch (Exception ex)
            {
                menu.Items.Add(new ToolStripMenuItem("Status unavailable: " + ex.Message) { Enabled = false });
            }
            menu.Items.Add(new ToolStripSeparator());

            var master = new ToolStripMenuItem("Master browser");
            foreach (string[] choice in MasterChoices)
            {
                string id = choice[0];
                var item = new ToolStripMenuItem(choice[1]) { Checked = config.MasterBrowser == id };
                item.Click += (s, e) => SetMaster(id);
                master.DropDownItems.Add(item);
            }
            menu.Items.Add(master);

            var backup = new ToolStripMenuItem("Back up now");
            backup.Click += (s, e) => { ShowWindow(); window.BackUpNow(); };
            menu.Items.Add(backup);

            var startup = new ToolStripMenuItem("Start with Windows") { Checked = config.StartWithWindows, CheckOnClick = false };
            startup.Click += (s, e) => SetStartup(!config.StartWithWindows);
            menu.Items.Add(startup);

            menu.Items.Add(new ToolStripSeparator());
            var exit = new ToolStripMenuItem("Exit");
            exit.Click += (s, e) => ExitApp();
            menu.Items.Add(exit);
        }

        /// <summary>Changes the master browser and makes every browser re-check against the new choice.</summary>
        private void SetMaster(string id)
        {
            try
            {
                Config config = Config.Load();
                if (config.MasterBrowser == id) return;
                config.MasterBrowser = id;
                config.Save();
                Env.WithStateLock(() =>
                {
                    State state = State.Load();
                    state.Revision++;
                    state.Save();
                });
                if (window != null && !window.IsDisposed) window.RefreshAll();
            }
            catch (Exception ex)
            {
                Theme.Error("The master browser was not changed. " + ex.Message);
            }
        }

        private void SetStartup(bool enabled)
        {
            try
            {
                Config config = Config.Load();
                config.StartWithWindows = enabled;
                config.Save();
                Installer.ApplyStartup(enabled);
            }
            catch (Exception ex)
            {
                Theme.Error("Start with Windows was not changed. " + ex.Message);
            }
        }

        /// <summary>At the first minute after midnight, takes the daily (and week, month, year) backups from what each browser last synced.</summary>
        private void DailyBackups()
        {
            string today = DateTime.Now.ToString("yyyyMMdd");
            if (today == lastBackupDay) return;
            lastBackupDay = today;
            try
            {
                Config config = Config.Load();
                Env.WithStateLock(() =>
                {
                    State state = State.Load();
                    foreach (string browser in state.Browsers.Keys.OrderBy(k => k).ToList())
                    {
                        Snap snapshot = state.Browsers[browser].Snapshot;
                        if (snapshot != null) Backups.Scheduled(state, browser, snapshot, false, config);
                    }
                    state.Save();
                });
                if (window != null && !window.IsDisposed && window.Visible) window.RefreshAll();
            }
            catch (Exception ex)
            {
                Env.Log("Midnight backup failed: " + ex.Message);
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                showWait.Unregister(null);
                exitWait.Unregister(null);
                showEvent.Dispose();
                exitEvent.Dispose();
                clock.Dispose();
                icon.Dispose();
                menu.Dispose();
                marshal.Dispose();
            }
            base.Dispose(disposing);
        }
    }
}
