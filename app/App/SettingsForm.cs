using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Windows.Forms;

namespace BookmarkSync
{
    /// <summary>Settings in tabs: Sync (duplicates, master browser), Backups (where they go, how many) and App (start, tray, exit, repair, remove).</summary>
    public sealed class SettingsForm : Form
    {
        private static readonly string[][] MasterChoices =
        {
            new[] { "off", "Off - every browser is equal" },
            new[] { "auto", "Automatic - Firefox, then Chrome, then Edge" },
            new[] { "firefox", "Firefox" },
            new[] { "chrome", "Chrome" },
            new[] { "edge", "Edge" }
        };

        private readonly Config config = Config.Load();
        private readonly Config original;
        private readonly CheckBox dedupe = new CheckBox();
        private readonly CheckBox ask = new CheckBox();
        private readonly ComboBox master = new ComboBox();
        private readonly NumericUpDown keep = new NumericUpDown();
        private readonly TextBox backupFolder = new TextBox();
        private readonly CheckBox startup = new CheckBox();
        private readonly CheckBox tray = new CheckBox();

        /// <summary>True when the user chose to remove everything.</summary>
        public bool Removed;

        public SettingsForm()
        {
            original = config.Copy();
            Text = Env.AppTitle + " - Settings";
            ClientSize = new Size(440, 560);
            StartPosition = FormStartPosition.CenterParent;
            Theme.Chrome(this);

            var tabs = new TabControl { Location = new Point(12, 14), Size = new Size(416, 420) };
            tabs.TabPages.Add(SyncTab());
            tabs.TabPages.Add(BackupsTab());
            tabs.TabPages.Add(AppTab());
            Controls.Add(tabs);

            // Help comes first at the bottom left, then the files, then Reset.
            var help = Theme.Button("Help", 16, 448, 70);
            help.Click += (s, e) => Process.Start(new ProcessStartInfo(Env.HelpUrl) { UseShellExecute = true });
            Controls.Add(help);
            var openSettings = Theme.Button("Open settings file", 94, 448, 130);
            openSettings.Click += (s, e) =>
            {
                if (!File.Exists(Env.ConfigFile)) config.Save();
                Process.Start("notepad.exe", "\"" + Env.ConfigFile + "\"");
            };
            Controls.Add(openSettings);
            var openLog = Theme.Button("Open log", 232, 448, 90);
            openLog.Click += (s, e) => { if (File.Exists(Env.LogPath)) Process.Start("notepad.exe", "\"" + Env.LogPath + "\""); };
            Controls.Add(openLog);
            var defaults = Theme.Button("Reset", 330, 448, 94);
            defaults.Click += (s, e) => ResetToDefaults();
            Controls.Add(defaults);

            var save = Theme.Button("Save", 16, 492, 90, primary: true);
            save.Click += (s, e) => SaveAndClose();
            Controls.Add(save);
            AcceptButton = save;
            var close = Theme.Button("Close", 314, 492);
            close.DialogResult = DialogResult.Cancel;
            Controls.Add(close);
            CancelButton = close;
        }

        private static TabPage NewPage(string title)
        {
            return new TabPage(title) { BackColor = Theme.WindowBg, Padding = new Padding(0) };
        }

        private TabPage SyncTab()
        {
            TabPage page = NewPage("Sync");
            page.Controls.Add(Theme.Heading("Duplicates", 12, 16));
            ask.Text = "Ask before a new duplicate stays";
            ask.AutoSize = true;
            ask.Location = new Point(12, 42);
            ask.Checked = config.AskAboutDuplicates;
            page.Controls.Add(ask);
            page.Controls.Add(Theme.SmallPrint("When you add a bookmark that already exists, the browser shows where the other one is and lets you keep or remove it. Kept duplicates sync to the other browsers.", 30, 66, 360, 44));
            dedupe.Text = "Delete duplicate bookmarks";
            dedupe.AutoSize = true;
            dedupe.Location = new Point(12, 120);
            dedupe.Checked = config.RemoveDuplicates;
            page.Controls.Add(dedupe);
            page.Controls.Add(Theme.SmallPrint("Off by default. When on, one copy of each address is kept, preferring the bookmarks bar.", 30, 144, 360));

            page.Controls.Add(Theme.Heading("Master browser", 12, 196));
            master.DropDownStyle = ComboBoxStyle.DropDownList;
            master.Location = new Point(12, 222);
            master.Width = 380;
            foreach (string[] choice in MasterChoices) master.Items.Add(choice[1]);
            master.SelectedIndex = Math.Max(0, Array.FindIndex(MasterChoices, c => c[0] == config.MasterBrowser));
            page.Controls.Add(master);
            page.Controls.Add(Theme.SmallPrint("The other browsers copy the master browser: its adds, deletes, moves, renames and order. Changes made in the others are undone.", 12, 250, 380, 44));
            return page;
        }

        private TabPage BackupsTab()
        {
            TabPage page = NewPage("Backups");
            page.Controls.Add(Theme.Heading("Backup folder", 12, 16));
            backupFolder.ReadOnly = true;
            backupFolder.Location = new Point(12, 42);
            backupFolder.Width = 290;
            backupFolder.BackColor = Theme.CardBg;
            backupFolder.Text = string.IsNullOrEmpty(config.BackupFolder) ? Env.DefaultBackupDir : config.BackupFolder;
            page.Controls.Add(backupFolder);
            var browse = Theme.Button("Browse", 310, 40, 82);
            browse.Click += (s, e) =>
            {
                using (var dialog = new FolderBrowserDialog())
                {
                    dialog.Description = "Choose where Bookmarks Sync Plus keeps its backups. A cloud-synced folder or a network share works.";
                    dialog.SelectedPath = backupFolder.Text;
                    if (dialog.ShowDialog(this) == DialogResult.OK) backupFolder.Text = dialog.SelectedPath;
                }
            };
            page.Controls.Add(browse);
            var useDefault = Theme.Button("Use the app folder", 12, 76, 150);
            useDefault.Click += (s, e) => backupFolder.Text = Env.DefaultBackupDir;
            page.Controls.Add(useDefault);
            var openFolder = Theme.Button("Open folder", 170, 76, 110);
            openFolder.Click += (s, e) =>
            {
                string folder = backupFolder.Text;
                if (Directory.Exists(folder)) Process.Start("explorer.exe", "\"" + folder + "\"");
                else Theme.Error("That folder does not exist yet. Save the setting first.");
            };
            page.Controls.Add(openFolder);
            page.Controls.Add(Theme.SmallPrint("The folder is checked when you save. If it is not reachable later (a network share that is off, for example) backups are skipped and the reason is written to the log.", 12, 114, 380, 44));

            page.Controls.Add(Theme.Heading("How many", 12, 176));
            page.Controls.Add(new Label { Text = "Keep the last", AutoSize = true, Location = new Point(12, 206) });
            keep.Minimum = 5;
            keep.Maximum = 500;
            keep.Value = Math.Max(5, Math.Min(500, config.AutoBackupsToKeep));
            keep.Location = new Point(100, 203);
            keep.Width = 64;
            page.Controls.Add(keep);
            page.Controls.Add(new Label { Text = "backups made before changes, per browser", AutoSize = true, Location = new Point(170, 206) });
            page.Controls.Add(Theme.SmallPrint("Each browser also keeps 7 daily, 4 weekly, 12 monthly and 5 yearly backups. Ones you make are always kept.", 12, 234, 380));
            return page;
        }

        private TabPage AppTab()
        {
            TabPage page = NewPage("App");
            page.Controls.Add(Theme.Heading("Start and close", 12, 16));
            startup.Text = "Start with Windows";
            startup.AutoSize = true;
            startup.Location = new Point(12, 42);
            startup.Checked = config.StartWithWindows;
            page.Controls.Add(startup);
            tray.Text = "Minimise to tray";
            tray.AutoSize = true;
            tray.Location = new Point(12, 68);
            tray.Checked = config.MinimiseToTray;
            page.Controls.Add(tray);
            page.Controls.Add(Theme.SmallPrint("On: the close button hides the window to the tray (no minimise button). Off: both buttons show and close exits. Exit in the tray icon's right-click menu and the button below always stop it.", 30, 92, 360, 56));
            var exitApp = Theme.Button("Exit app", 12, 156, 110);
            exitApp.Click += (s, e) =>
            {
                if (TrayApp.Current != null) TrayApp.Current.ExitApp();
            };
            page.Controls.Add(exitApp);

            page.Controls.Add(Theme.Heading("Install", 12, 214));
            var repair = Theme.Button("Repair install", 12, 240, 120);
            repair.Click += (s, e) =>
            {
                try
                {
                    var changes = new List<string>();
                    Installer.Sync(changes);
                    Env.Log("Repaired: " + string.Join(", ", changes));
                    MessageBox.Show("Complete - the browser registration, extension files and shortcuts were rewritten.", Env.AppTitle);
                }
                catch (Exception ex)
                {
                    Theme.Error("The repair did not finish. " + ex.Message);
                }
            };
            page.Controls.Add(repair);
            var remove = Theme.Button("Remove all app data", 140, 240, 160, warning: true);
            remove.Click += (s, e) => RemoveEverything();
            page.Controls.Add(remove);
            page.Controls.Add(Theme.SmallPrint("Repair rewrites the browser registration and shortcuts. Remove takes Bookmarks Sync Plus off this computer.", 12, 276, 380));
            return page;
        }

        private void ResetToDefaults()
        {
            if (!Theme.Confirm("Put every setting back to its default? Bookmarks and backups are not touched.", "Reset")) return;
            var d = new Config();
            dedupe.Checked = d.RemoveDuplicates;
            ask.Checked = d.AskAboutDuplicates;
            startup.Checked = d.StartWithWindows;
            tray.Checked = d.MinimiseToTray;
            keep.Value = d.AutoBackupsToKeep;
            master.SelectedIndex = 0;
            backupFolder.Text = Env.DefaultBackupDir;
        }

        private void SaveAndClose()
        {
            config.RemoveDuplicates = dedupe.Checked;
            config.StartWithWindows = startup.Checked;
            config.MinimiseToTray = tray.Checked;
            config.AskAboutDuplicates = ask.Checked;
            config.AutoBackupsToKeep = (int)keep.Value;
            config.MasterBrowser = MasterChoices[master.SelectedIndex][0];

            // The default folder is stored as empty so the setting follows the app folder.
            string chosen = backupFolder.Text.Trim();
            config.BackupFolder = string.Equals(chosen, Env.DefaultBackupDir, StringComparison.OrdinalIgnoreCase) ? "" : chosen;
            string oldFolder = string.IsNullOrEmpty(original.BackupFolder) ? Env.DefaultBackupDir : original.BackupFolder;
            string newFolder = string.IsNullOrEmpty(config.BackupFolder) ? Env.DefaultBackupDir : config.BackupFolder;
            bool folderChanged = !string.Equals(oldFolder, newFolder, StringComparison.OrdinalIgnoreCase);
            if (folderChanged)
            {
                string problem = Backups.CheckFolder(newFolder);
                if (problem != null)
                {
                    Theme.Error("That backup folder cannot be used, nothing was saved. " + problem);
                    return;
                }
            }

            try
            {
                if (folderChanged && Directory.Exists(oldFolder) && Directory.GetFiles(oldFolder, "bookmarks-*").Length > 0
                    && Theme.Confirm("Copy the existing backups to the new folder? The old ones stay where they are.", "Backup folder"))
                {
                    int copied = Backups.CopyAll(oldFolder, newFolder);
                    Env.Log("Copied " + copied + " backup files to " + newFolder);
                }
                config.Save();
                // A change to how the master list is built makes every browser re-check.
                if (original.MasterBrowser != config.MasterBrowser || original.RemoveDuplicates != config.RemoveDuplicates)
                {
                    Env.WithStateLock(() =>
                    {
                        State state = State.Load();
                        state.Revision++;
                        state.Save();
                    });
                }
                if (original.StartWithWindows != config.StartWithWindows) Installer.ApplyStartup(config.StartWithWindows);
                Env.Log("Settings saved");
                Close();
            }
            catch (Exception ex)
            {
                Theme.Error("Settings were not saved. " + ex.Message);
            }
        }

        private void RemoveEverything()
        {
            string message = "This takes Bookmarks Sync Plus off this computer:\r\n\r\n" +
                "- the browsers stop being able to sync (remove the extension yourself)\r\n" +
                "- the Desktop, Start menu and Startup shortcuts are removed\r\n" +
                "- the app folder, including backups kept in it, goes to the Recycle Bin\r\n" +
                "- backups kept in a folder you chose are left where they are\r\n\r\n" +
                "Your bookmarks in each browser stay as they are. Carry on?";
            if (!Theme.Confirm(message, "Remove all app data")) return;
            try
            {
                Removed = true;
                Installer.Uninstall();
                Close();
                Application.Exit();
            }
            catch (Exception ex)
            {
                Removed = false;
                Theme.Error("Could not remove everything. " + ex.Message);
            }
        }
    }
}
