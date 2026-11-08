using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Windows.Forms;

namespace BookmarkSync
{
    /// <summary>Settings: duplicates, master browser, backups, start with Windows, help, reset and removal.</summary>
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
        private readonly CheckBox startup = new CheckBox();
        private readonly CheckBox ask = new CheckBox();
        private readonly ComboBox master = new ComboBox();
        private readonly NumericUpDown keep = new NumericUpDown();

        /// <summary>True when the user chose to remove everything.</summary>
        public bool Removed;

        public SettingsForm()
        {
            original = config.Copy();
            Text = Env.AppTitle + " - Settings";
            ClientSize = new Size(440, 560);
            StartPosition = FormStartPosition.CenterParent;
            Theme.Chrome(this);

            Controls.Add(Theme.Heading("Duplicates", 16, 20));
            dedupe.Text = "Delete duplicate bookmarks";
            dedupe.AutoSize = true;
            dedupe.Location = new Point(16, 44);
            dedupe.Checked = config.RemoveDuplicates;
            Controls.Add(dedupe);
            Controls.Add(Theme.SmallPrint("Off by default. When on, one copy of each address is kept, preferring the bookmarks bar.", 34, 68, 390));

            Controls.Add(Theme.Heading("Master browser", 16, 104));
            master.DropDownStyle = ComboBoxStyle.DropDownList;
            master.Location = new Point(16, 130);
            master.Width = 400;
            foreach (string[] choice in MasterChoices) master.Items.Add(choice[1]);
            master.SelectedIndex = Math.Max(0, Array.FindIndex(MasterChoices, c => c[0] == config.MasterBrowser));
            Controls.Add(master);
            Controls.Add(Theme.SmallPrint("The other browsers copy the master browser: its adds, deletes, moves, renames and order. Changes made in the others are undone.", 16, 158, 410));

            Controls.Add(Theme.Heading("Backups", 16, 204));
            Controls.Add(new Label { Text = "Keep the last", AutoSize = true, Location = new Point(16, 232) });
            keep.Minimum = 5;
            keep.Maximum = 500;
            keep.Value = Math.Max(5, Math.Min(500, config.AutoBackupsToKeep));
            keep.Location = new Point(104, 229);
            keep.Width = 64;
            Controls.Add(keep);
            Controls.Add(new Label { Text = "backups made before changes, per browser", AutoSize = true, Location = new Point(174, 232) });
            Controls.Add(Theme.SmallPrint("Each browser also keeps 7 daily, 4 weekly, 12 monthly and 5 yearly backups. Ones you make are always kept.", 16, 258, 400));

            Controls.Add(Theme.Heading("Start", 16, 304));
            startup.Text = "Start with Windows";
            startup.AutoSize = true;
            startup.Location = new Point(16, 328);
            startup.Checked = config.StartWithWindows;
            Controls.Add(startup);
            Controls.Add(Theme.SmallPrint("Runs in the tray so browsers are told the moment another one changes.", 34, 352, 390));

            Controls.Add(Theme.Heading("Help and files", 16, 388));
            var help = Theme.Button("Help", 16, 414, 90);
            help.Click += (s, e) => new HelpForm().ShowDialog(this);
            Controls.Add(help);
            var openSettings = Theme.Button("Open settings file", 114, 414, 140);
            openSettings.Click += (s, e) =>
            {
                if (!File.Exists(Env.ConfigFile)) config.Save();
                Process.Start("notepad.exe", "\"" + Env.ConfigFile + "\"");
            };
            Controls.Add(openSettings);
            var openLog = Theme.Button("Open log", 262, 414, 90);
            openLog.Click += (s, e) => { if (File.Exists(Env.LogPath)) Process.Start("notepad.exe", "\"" + Env.LogPath + "\""); };
            Controls.Add(openLog);

            var defaults = Theme.Button("Restore defaults", 16, 460, 140);
            defaults.Click += (s, e) =>
            {
                if (!Theme.Confirm("Put every setting back to its default? Bookmarks and backups are not touched.", "Restore defaults")) return;
                var d = new Config();
                dedupe.Checked = d.RemoveDuplicates;
                ask.Checked = d.AskAboutDuplicates;
                startup.Checked = d.StartWithWindows;
                keep.Value = d.AutoBackupsToKeep;
                master.SelectedIndex = 0;
            };
            Controls.Add(defaults);
            var remove = Theme.Button("Remove all app data", 164, 460, 160, warning: true);
            remove.Click += (s, e) => RemoveEverything();
            Controls.Add(remove);
            var repair = Theme.Button("Repair install", 332, 460, 92);
            repair.Click += (s, e) =>
            {
                try
                {
                    var changes = new List<string>();
                    Installer.Sync(changes);
                    Env.Log("Repaired: " + string.Join(", ", changes));
                    MessageBox.Show("Complete - the browser registration and shortcuts were rewritten.", Env.AppTitle);
                }
                catch (Exception ex)
                {
                    Theme.Error("The repair did not finish. " + ex.Message);
                }
            };
            Controls.Add(repair);

            // Room for the question about new duplicates under the duplicates section.
            foreach (Control control in Controls)
            {
                if (control.Top >= 100) control.Top += 56;
            }
            ClientSize = new Size(440, 616);
            ask.Text = "Ask before a new duplicate stays";
            ask.AutoSize = true;
            ask.Location = new Point(16, 104);
            ask.Checked = config.AskAboutDuplicates;
            Controls.Add(ask);
            Controls.Add(Theme.SmallPrint("When you add a bookmark that already exists, the browser shows where the other one is and lets you keep or remove it. Kept duplicates sync to the other browsers.", 34, 128, 390, 44));

            var save = Theme.Button("Save", 16, 512, 90, primary: true);
            save.Click += (s, e) => SaveAndClose();
            Controls.Add(save);
            AcceptButton = save;
            var close = Theme.Button("Close", 314, 512);
            close.DialogResult = DialogResult.Cancel;
            Controls.Add(close);
            CancelButton = close;
        }

        private void SaveAndClose()
        {
            config.RemoveDuplicates = dedupe.Checked;
            config.StartWithWindows = startup.Checked;
            config.AskAboutDuplicates = ask.Checked;
            config.AutoBackupsToKeep = (int)keep.Value;
            config.MasterBrowser = MasterChoices[master.SelectedIndex][0];
            try
            {
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
            string message = "This takes Bookmark Sync off this computer:\r\n\r\n" +
                "- the browsers stop being able to sync (remove the extension yourself)\r\n" +
                "- the Desktop, Start menu and Startup shortcuts are removed\r\n" +
                "- the app folder, including all backups, goes to the Recycle Bin\r\n\r\n" +
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
