using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Windows.Forms;

namespace BookmarkSync
{
    /// <summary>The main window: browsers, backups, Back up now, Settings, Close (which hides it to the tray).</summary>
    public sealed class MainForm : Form
    {
        private readonly TrayApp app;
        private readonly Label masterLabel = new Label();
        private readonly Label statusLabel = new Label();
        private ListView browserList;
        private ListView backupList;
        private readonly Timer refreshTimer = new Timer { Interval = 10000 };

        public MainForm(TrayApp app)
        {
            this.app = app;
            Text = Env.AppTitle;
            ClientSize = new Size(560, 580);
            StartPosition = FormStartPosition.CenterScreen;
            Theme.Chrome(this);

            Controls.Add(Theme.Heading("Browsers", 16, 20));
            masterLabel.AutoSize = true;
            masterLabel.ForeColor = Theme.Muted;
            masterLabel.Location = new Point(300, 21);
            Controls.Add(masterLabel);

            browserList = Theme.CardList(16, 44, 528, 110, new object[] { "Browser", 110 }, new object[] { "Bookmarks", 90 }, new object[] { "Last sync", 120 }, new object[] { "Status", 186 });
            Controls.Add(browserList);
            Controls.Add(Theme.SmallPrint("Browsers sync the moment a bookmark changes and are told when another browser changes.", 16, 158, 380));

            var viewMaster = Theme.Button("View master list", 404, 160, 140);
            viewMaster.Click += (s, e) => new MasterForm().ShowDialog(this);
            Controls.Add(viewMaster);

            Controls.Add(Theme.Heading("Backups", 16, 206));
            backupList = Theme.CardList(16, 230, 528, 200, new object[] { "Date", 140 }, new object[] { "Browser", 120 }, new object[] { "Type", 246 });
            Controls.Add(backupList);

            var restore = Theme.Button("Restore", 16, 438, 90, warning: true);
            restore.Click += (s, e) => RestoreSelected();
            Controls.Add(restore);

            var openFolder = Theme.Button("Open folder", 114, 438, 110);
            openFolder.Click += (s, e) =>
            {
                Directory.CreateDirectory(Env.BackupDir);
                Process.Start("explorer.exe", "\"" + Env.BackupDir + "\"");
            };
            Controls.Add(openFolder);

            statusLabel.Location = new Point(16, 480);
            statusLabel.Size = new Size(528, 20);
            Controls.Add(statusLabel);

            var backupNow = Theme.Button("Back up now", 16, 512, 120, primary: true);
            backupNow.Click += (s, e) => BackUpNow();
            Controls.Add(backupNow);

            var settings = Theme.Button("Settings", 144, 512, 100);
            settings.Click += (s, e) =>
            {
                using (var form = new SettingsForm()) form.ShowDialog(this);
                ApplyTraySetting(Config.Load());
                RefreshAll();
            };
            Controls.Add(settings);

            var setup = Theme.Button("Set up browsers", 252, 512, 140);
            setup.Click += (s, e) =>
            {
                using (var form = new SetupForm()) form.ShowDialog(this);
                RefreshAll();
            };
            Controls.Add(setup);

            var close = Theme.Button("Close", 434, 512);
            close.Click += (s, e) => Close();
            Controls.Add(close);
            CancelButton = close;

            refreshTimer.Tick += (s, e) => { if (Visible) RefreshBrowsers(); };
            Shown += (s, e) =>
            {
                // Opened from a browser, the window can start behind it.
                TopMost = true;
                Activate();
                TopMost = false;
                refreshTimer.Start();
            };
            VisibleChanged += (s, e) =>
            {
                if (Visible) { RefreshAll(); refreshTimer.Start(); } else refreshTimer.Stop();
            };
            FormClosing += OnClosing;

            Config config = Config.Load();
            ApplyTraySetting(config);
            if (config.WindowLeft >= 0)
            {
                var spot = new Point(config.WindowLeft, config.WindowTop);
                if (Screen.AllScreens.Any(sc => sc.WorkingArea.Contains(spot)))
                {
                    StartPosition = FormStartPosition.Manual;
                    Location = spot;
                }
            }
            RefreshAll();
            if (config.Problem != null) SetStatus("The settings file has a problem, defaults are in use. Settings > Open settings file.", Theme.WarningFg);
        }

        private void OnClosing(object sender, FormClosingEventArgs e)
        {
            try
            {
                Config config = Config.Load();
                config.WindowLeft = Location.X;
                config.WindowTop = Location.Y;
                config.Save();
            }
            catch (Exception ex)
            {
                Env.Log("Window position not saved: " + ex.Message);
            }
            if (e.CloseReason != CloseReason.UserClosing || app.Exiting) return;
            if (Config.Load().MinimiseToTray)
            {
                // Closing the window leaves the program running in the tray.
                e.Cancel = true;
                Hide();
            }
            else
            {
                // Without the tray option the close button exits.
                e.Cancel = true;
                app.ExitApp();
            }
        }

        /// <summary>With the tray option on there is no minimise button, and the close button hides to the tray.</summary>
        private void ApplyTraySetting(Config config)
        {
            MinimizeBox = !config.MinimiseToTray;
        }

        public void SetStatus(string text, Color color)
        {
            statusLabel.Text = text;
            statusLabel.ForeColor = color;
        }

        public void RefreshAll()
        {
            RefreshBrowsers();
            RefreshBackups();
        }

        private void RefreshBrowsers()
        {
            browserList.Items.Clear();
            State state;
            try
            {
                state = State.Load();
            }
            catch (Exception ex)
            {
                SetStatus("The bookmark state could not be read. " + ex.Message, Theme.ErrorFg);
                return;
            }
            Config config = Config.Load();
            string source = Engine.MirrorSource(state, config, null);
            Status status = Status.Load();
            foreach (string browser in state.Browsers.Keys.OrderBy(k => k))
            {
                BrowserEntry entry = state.Browsers[browser];
                int count = entry.Snapshot == null ? 0 : entry.Snapshot.Bookmarks.Count;
                string text;
                if (state.PendingOverwrite.Contains(browser)) text = "Changes to match";
                else if (status.IsCurrent(browser)) text = "Up to date";
                else text = "Changes waiting";
                if (browser == source) text = "Master browser, " + text.ToLowerInvariant();
                var item = new ListViewItem(Env.BrowserName(browser)) { Tag = browser };
                item.SubItems.Add(count.ToString());
                item.SubItems.Add(Theme.FormatTime(entry.Seen));
                item.SubItems.Add(text);
                browserList.Items.Add(item);
            }
            masterLabel.Text = "Master list: " + state.Master.Bookmarks.Count + " bookmarks" + (source != null ? ", copies " + Env.BrowserName(source) : "");
        }

        private void RefreshBackups()
        {
            backupList.Items.Clear();
            foreach (BackupInfo backup in Backups.List())
            {
                var item = new ListViewItem(backup.When.ToString("d MMM yyyy HH:mm")) { Tag = backup.Name };
                item.SubItems.Add(backup.Browser);
                item.SubItems.Add(backup.Type);
                backupList.Items.Add(item);
            }
        }

        private void RestoreSelected()
        {
            if (backupList.SelectedItems.Count == 0)
            {
                SetStatus("Pick a backup in the list first.", Theme.WarningFg);
                return;
            }
            ListViewItem item = backupList.SelectedItems[0];
            string message = "Make the backup from " + item.Text + " the master list?\r\n\r\nThe current list is backed up first. Every browser changes to match straight away.";
            if (!Theme.Confirm(message, "Restore backup")) return;
            try
            {
                Env.WithStateLock(() =>
                {
                    State state = State.Load();
                    Backups.Restore(state, (string)item.Tag);
                    state.Save();
                });
                SetStatus("Complete - restored, each browser updates now.", Theme.OkFg);
                RefreshAll();
            }
            catch (Exception ex)
            {
                Theme.Error("The backup was not restored, nothing was changed. " + ex.Message);
            }
        }

        public void BackUpNow()
        {
            try
            {
                int saved = 0;
                Env.WithStateLock(() =>
                {
                    State state = State.Load();
                    foreach (string browser in state.Browsers.Keys.OrderBy(k => k))
                    {
                        Snap snapshot = state.Browsers[browser].Snapshot;
                        if (snapshot == null || snapshot.Bookmarks.Count == 0) continue;
                        Backups.Create(state, "manual", browser, snapshot);
                        saved++;
                    }
                });
                SetStatus(saved == 0 ? "No browser has synced yet, nothing to back up." : "Complete - " + saved + " backup(s) saved, one per browser.", saved == 0 ? Theme.WarningFg : Theme.OkFg);
                RefreshBackups();
            }
            catch (Exception ex)
            {
                Theme.Error("The backup was not saved. " + ex.Message);
            }
        }
    }
}
