using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Windows.Forms;
using Microsoft.Win32;

namespace BookmarkSync
{
    /// <summary>
    /// Helps add the extension to each browser: opens the right page and puts the extension folder on
    /// the clipboard. Browsers do not let a program install an extension from outside their store, so
    /// the last click is the person's own.
    /// </summary>
    public sealed class SetupForm : Form
    {
        private readonly Label message = new Label();

        public SetupForm()
        {
            Text = Env.AppTitle + " - Set up browsers";
            ClientSize = new Size(500, 470);
            StartPosition = FormStartPosition.CenterParent;
            Theme.Chrome(this);

            Controls.Add(Theme.Heading("Browsers", 16, 20));
            Controls.Add(Theme.SmallPrint("Each button opens that browser's extensions page. Bookmarks Sync Plus keeps its extension files up to date in its own folder.", 16, 44, 468));

            State state = null;
            try { state = State.Load(); } catch (Exception) { }
            AddRow("chrome", "Chrome", @"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\chrome.exe", "chromium", "chrome://extensions", 90, state);
            AddRow("edge", "Edge", @"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\msedge.exe", "chromium", "edge://extensions", 130, state);
            AddRow("firefox", "Firefox", @"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\firefox.exe", "firefox", "about:debugging#/runtime/this-firefox", 170, state);

            Controls.Add(Theme.Heading("Steps", 16, 222));
            Controls.Add(Theme.SmallPrint(
                "Chrome and Edge: the folder path is copied for you. On the page that opens, turn on Developer mode, choose Load unpacked, paste the path and pick the folder. After a Bookmarks Sync Plus update, press Reload on the extension.\r\n\r\n" +
                "Firefox: put the signed Bookmarks Sync Plus .xpi file in the Bookmarks Sync Plus folder (Open folder) or next to the program, then press the button and accept the prompt. Without a signed file, Firefox only allows a temporary add-on: choose Load Temporary Add-on and pick manifest.json in the copied folder.",
                16, 246, 468, 130));

            message.AutoSize = false;
            message.Location = new Point(16, 372);
            message.Size = new Size(468, 20);
            Controls.Add(message);

            var folder = Theme.Button("Open folder", 16, 398, 110);
            folder.Click += (s, e) =>
            {
                Directory.CreateDirectory(Env.DataDir);
                Process.Start("explorer.exe", "\"" + Env.DataDir + "\"");
            };
            Controls.Add(folder);
            var close = Theme.Button("Close", 374, 398);
            close.DialogResult = DialogResult.Cancel;
            Controls.Add(close);
            CancelButton = close;
        }

        private void AddRow(string id, string name, string appPath, string flavour, string page, int y, State state)
        {
            Controls.Add(new Label { Text = name, AutoSize = true, Location = new Point(16, y + 6), Font = Theme.UiFontBold });
            BrowserEntry seen;
            // Only counts as syncing if the extension has reported in recently (the entry stays in the state after an add-on is removed).
            bool working = state != null && state.Browsers.TryGetValue(id, out seen) && seen != null && Env.UnixTime() - seen.Seen < 24 * 3600;
            bool wasSeen = state != null && state.Browsers.ContainsKey(id);
            Controls.Add(new Label
            {
                Text = working ? "Syncing" : (wasSeen ? "Not seen recently" : "Not set up yet"),
                AutoSize = true,
                Location = new Point(110, y + 6),
                ForeColor = working ? Theme.OkFg : Theme.Muted
            });
            var button = Theme.Button(working ? "Set up again" : "Set up " + name, 330, y, 154, primary: !working);
            button.Click += (s, e) => Setup(id, name, appPath, flavour, page);
            Controls.Add(button);
        }

        private static string FindBrowser(string appPath)
        {
            foreach (RegistryKey root in new[] { Registry.CurrentUser, Registry.LocalMachine })
            {
                using (RegistryKey key = root.OpenSubKey(appPath))
                {
                    string path = key == null ? null : key.GetValue("") as string;
                    if (!string.IsNullOrEmpty(path) && File.Exists(path)) return path;
                }
            }
            return null;
        }

        private static string FindSignedFirefoxFile()
        {
            var places = new List<string> { Env.DataDir, Path.GetDirectoryName(Installer.SelfPath) };
            foreach (string place in places)
            {
                if (!Directory.Exists(place)) continue;
                foreach (string file in Directory.GetFiles(place, "*.xpi"))
                {
                    string name = Path.GetFileName(file).ToLowerInvariant();
                    if (name.Contains("bookmark")) return file;
                }
            }
            return null;
        }

        private void Setup(string id, string name, string appPath, string flavour, string page)
        {
            try
            {
                var changes = new List<string>();
                Installer.WriteExtensions(changes);
                string browser = FindBrowser(appPath);
                if (browser == null)
                {
                    SetMessage(name + " was not found on this computer.", Theme.WarningFg);
                    return;
                }
                string folder = Installer.ExtensionDir(flavour);
                if (id == "firefox")
                {
                    string signed = FindSignedFirefoxFile();
                    if (signed != null)
                    {
                        Process.Start(browser, "\"" + signed + "\"");
                        SetMessage("Complete - accept the install prompt in Firefox.", Theme.OkFg);
                        return;
                    }
                    Clipboard.SetText(Path.Combine(folder, "manifest.json"));
                    Process.Start(browser, page);
                    SetMessage("Complete - Load Temporary Add-on, paste the path (copied).", Theme.OkFg);
                    return;
                }
                Clipboard.SetText(folder);
                Process.Start(browser, page);
                SetMessage("Complete - turn on Developer mode, Load unpacked, paste the path (copied).", Theme.OkFg);
            }
            catch (Exception ex)
            {
                Theme.Error("Could not open " + name + ". " + ex.Message);
            }
        }

        private void SetMessage(string text, Color color)
        {
            message.Text = text;
            message.ForeColor = color;
        }
    }
}
