using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text;
using System.Threading;
using Microsoft.Win32;

namespace BookmarkSync
{
    /// <summary>
    /// Puts the program on this computer for the current user (no admin rights): the exe in the data
    /// folder, the browsers' registration of the helper, shortcuts and the version stamp. Everything
    /// is changed only when it differs, so running it again is harmless.
    /// </summary>
    public static class Installer
    {
        private const string RegKey = @"Software\BookmarkSync";

        private static readonly string[][] BrowserKeys =
        {
            new[] { @"Software\Google\Chrome\NativeMessagingHosts\" + Env.HostName, "host-chromium.json" },
            new[] { @"Software\Microsoft\Edge\NativeMessagingHosts\" + Env.HostName, "host-chromium.json" },
            new[] { @"Software\Mozilla\NativeMessagingHosts\" + Env.HostName, "host-firefox.json" }
        };

        public static string SelfPath { get { return Assembly.GetExecutingAssembly().Location; } }

        public static bool RunningInstalledCopy
        {
            get { return string.Equals(SelfPath, Env.InstalledExe, StringComparison.OrdinalIgnoreCase); }
        }

        public static Version InstalledVersion()
        {
            try
            {
                using (RegistryKey key = Registry.CurrentUser.OpenSubKey(RegKey))
                {
                    string text = key == null ? null : key.GetValue("Version") as string;
                    return text == null ? null : new Version(text);
                }
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// Installs or upgrades from the copy that was started, when it is not the installed one.
        /// Returns true when the installed copy has been started and this one should exit.
        /// </summary>
        public static bool InstallAndHandOver(bool force, out string note)
        {
            note = null;
            Version installed = InstalledVersion();
            Version mine = new Version(Env.AppVersion);
            bool missing = installed == null || !File.Exists(Env.InstalledExe);
            bool upgrade = installed != null && mine > installed;
            if (!force && !missing && !upgrade)
            {
                // The same or an older copy changes nothing; just open the installed one.
                StartInstalled("--show");
                return true;
            }

            var changes = new List<string>();
            Directory.CreateDirectory(Env.DataDir);
            AskRunningCopyToExit();
            CopyExe(changes);
            Sync(changes);
            using (RegistryKey key = Registry.CurrentUser.CreateSubKey(RegKey))
            {
                key.SetValue("Version", Env.AppVersion);
            }
            if (missing) note = "installed";
            else note = "updated to " + Env.AppVersion + " (" + string.Join(", ", changes) + ")";
            using (RegistryKey key = Registry.CurrentUser.CreateSubKey(RegKey))
            {
                key.SetValue("StartupNote", note);
            }
            StartInstalled("--show");
            return true;
        }

        /// <summary>The note left by the last install or upgrade, shown once.</summary>
        public static string TakeStartupNote()
        {
            try
            {
                using (RegistryKey key = Registry.CurrentUser.OpenSubKey(RegKey, true))
                {
                    if (key == null) return null;
                    string note = key.GetValue("StartupNote") as string;
                    if (note != null) key.DeleteValue("StartupNote", false);
                    return note;
                }
            }
            catch
            {
                return null;
            }
        }

        private static void StartInstalled(string argument)
        {
            Process.Start(new ProcessStartInfo(Env.InstalledExe, argument) { UseShellExecute = false });
        }

        /// <summary>A running copy (and browsers' helper processes) lock the exe, but a locked exe can be renamed.</summary>
        private static void CopyExe(List<string> changes)
        {
            if (File.Exists(Env.InstalledExe))
            {
                string aside = Env.InstalledExe + "." + DateTime.Now.Ticks + ".old";
                try { File.Move(Env.InstalledExe, aside); } catch (IOException) { }
            }
            File.Copy(SelfPath, Env.InstalledExe, true);
            changes.Add("program");
        }

        /// <summary>Removes copies of the program left aside by an earlier upgrade, once nothing holds them.</summary>
        public static void CleanOldCopies()
        {
            try
            {
                foreach (string old in Directory.GetFiles(Env.DataDir, "BookmarkSync.exe.*.old"))
                {
                    try { File.Delete(old); } catch (IOException) { } catch (UnauthorizedAccessException) { }
                }
                foreach (string leftover in new[] { "launch.vbs", "BookmarkSyncHost.cmd", "BookmarkSync.ps1", "BookmarkSync.ico" })
                {
                    string path = Path.Combine(Env.DataDir, leftover);
                    if (File.Exists(path)) try { File.Delete(path); } catch (IOException) { }
                }
            }
            catch
            {
                // Tidying is optional.
            }
        }

        private static void AskRunningCopyToExit()
        {
            try
            {
                using (EventWaitHandle exit = EventWaitHandle.OpenExisting("BookmarkSyncExit"))
                {
                    exit.Set();
                }
                Thread.Sleep(1500);
            }
            catch (WaitHandleCannotBeOpenedException)
            {
                // Nothing running.
            }
        }

        /// <summary>
        /// Brings registration and shortcuts in line with this copy: the browsers' helper manifests and
        /// registry keys, shortcuts on the Desktop, in the Start menu and the folder the program was
        /// first run from, and the Startup entry. Only what differs is rewritten.
        /// </summary>
        public static void Sync(List<string> changes)
        {
            Directory.CreateDirectory(Env.DataDir);
            string chromium = Manifest(true);
            string firefox = Manifest(false);
            foreach (string[] entry in BrowserKeys)
            {
                string manifestPath = Path.Combine(Env.DataDir, entry[1]);
                string text = entry[1] == "host-chromium.json" ? chromium : firefox;
                if (!File.Exists(manifestPath) || Env.ReadText(manifestPath) != text)
                {
                    Env.WriteFileSafely(manifestPath, text);
                    changes.Add("helper description");
                }
                using (RegistryKey key = Registry.CurrentUser.CreateSubKey(entry[0]))
                {
                    if ((key.GetValue("") as string) != manifestPath)
                    {
                        key.SetValue("", manifestPath);
                        changes.Add("browser registration");
                    }
                }
            }

            Config config = Config.Load();
            string exe = Env.InstalledExe;
            SetShortcut(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), exe, "--show", changes);
            SetShortcut(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs)), exe, "--show", changes);
            string startup = Environment.GetFolderPath(Environment.SpecialFolder.Startup);
            string startupLink = Path.Combine(startup, Env.AppTitle + ".lnk");
            if (config.StartWithWindows) SetShortcut(startup, exe, "--tray", changes);
            else if (File.Exists(startupLink)) { File.Delete(startupLink); changes.Add("startup entry"); }

            // Beside the copy that was run: it opens that copy, so a newer download upgrades when used.
            if (!RunningInstalledCopy) SetShortcut(Path.GetDirectoryName(SelfPath), SelfPath, "--show", changes);
        }

        private static string Manifest(bool chromium)
        {
            var d = new Dictionary<string, object>();
            d["name"] = Env.HostName;
            d["description"] = "Bookmark Sync helper";
            d["path"] = Env.InstalledExe;
            d["type"] = "stdio";
            if (chromium) d["allowed_origins"] = new List<object> { "chrome-extension://" + Env.ChromiumExtensionId + "/" };
            else d["allowed_extensions"] = new List<object> { Env.FirefoxExtensionId };
            return Json.Write(d);
        }

        /// <summary>Makes a shortcut only when it is missing or differs.</summary>
        private static void SetShortcut(string folder, string target, string arguments, List<string> changes)
        {
            if (string.IsNullOrEmpty(folder) || !Directory.Exists(folder)) return;
            string file = Path.Combine(folder, Env.AppTitle + ".lnk");
            Type shellType = Type.GetTypeFromProgID("WScript.Shell");
            dynamic shell = Activator.CreateInstance(shellType);
            dynamic link = shell.CreateShortcut(file);
            if (File.Exists(file) && (string)link.TargetPath == target && (string)link.Arguments == arguments) return;
            link.TargetPath = target;
            link.Arguments = arguments;
            link.WorkingDirectory = Path.GetDirectoryName(target);
            link.IconLocation = target + ",0";
            link.Description = "Open Bookmark Sync";
            link.Save();
            if (!changes.Contains("shortcuts")) changes.Add("shortcuts");
        }

        /// <summary>Applies the Start with Windows setting without a full install pass.</summary>
        public static void ApplyStartup(bool enabled)
        {
            string startup = Environment.GetFolderPath(Environment.SpecialFolder.Startup);
            string link = Path.Combine(startup, Env.AppTitle + ".lnk");
            if (enabled) SetShortcut(startup, Env.InstalledExe, "--tray", new List<string>());
            else if (File.Exists(link)) File.Delete(link);
        }

        /// <summary>
        /// Takes Bookmark Sync off this computer: registration, shortcuts, startup entry, and the data
        /// folder (backups included) to the Recycle Bin. The running exe is removed a moment later.
        /// </summary>
        public static void Uninstall()
        {
            foreach (string[] entry in BrowserKeys)
            {
                try { Registry.CurrentUser.DeleteSubKeyTree(entry[0], false); } catch (ArgumentException) { }
            }
            try { Registry.CurrentUser.DeleteSubKeyTree(RegKey, false); } catch (ArgumentException) { }
            foreach (string folder in new[]
            {
                Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
                Environment.GetFolderPath(Environment.SpecialFolder.Programs),
                Environment.GetFolderPath(Environment.SpecialFolder.Startup)
            })
            {
                string file = Path.Combine(folder, Env.AppTitle + ".lnk");
                if (File.Exists(file)) File.Delete(file);
            }

            // Everything except the running program goes to the Recycle Bin; the program follows.
            foreach (string path in Directory.GetFileSystemEntries(Env.DataDir))
            {
                if (string.Equals(path, Env.InstalledExe, StringComparison.OrdinalIgnoreCase)) continue;
                try
                {
                    if (Directory.Exists(path))
                        Microsoft.VisualBasic.FileIO.FileSystem.DeleteDirectory(path, Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs, Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin);
                    else
                        Microsoft.VisualBasic.FileIO.FileSystem.DeleteFile(path, Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs, Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin);
                }
                catch (IOException) { }
            }
            string command = "/c ping -n 4 127.0.0.1 >nul & del /f /q \"" + Env.InstalledExe + "\" & rmdir \"" + Env.DataDir + "\"";
            Process.Start(new ProcessStartInfo("cmd.exe", command) { CreateNoWindow = true, UseShellExecute = false, WindowStyle = ProcessWindowStyle.Hidden });
        }
    }
}
