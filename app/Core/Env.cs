using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;

namespace BookmarkSync
{
    /// <summary>Names, paths and small file helpers shared by every part of the program.</summary>
    public static class Env
    {
        public const string AppTitle = "Bookmark Sync";
        public const string AppVersion = "1.0.1";
        /// <summary>The README on GitHub, which is the help for every setting.</summary>
        public const string HelpUrl = "https://github.com/MithUK1/bookmark-sync#readme";
        public const string HostName = "com.muk1.bookmarksync";
        public const string ChromiumExtensionId = "kmajebfppgghbdedpfacpckkimnclejj";
        public const string FirefoxExtensionId = "bookmark-sync@muk1";

        /// <summary>Characters that never appear in a folder name or address, used to build keys.</summary>
        public const char PathSep = '\u001f';
        public const char KeySep = '\u001e';

        /// <summary>Largest piece of a reply sent to a browser (a native reply is capped at 1 MB).</summary>
        public const int PartBytes = 700000;

        public static readonly string[] BrowserIds = { "chrome", "edge", "firefox" };

        public static string BrowserName(string id)
        {
            switch (id)
            {
                case "chrome": return "Chrome";
                case "edge": return "Edge";
                case "firefox": return "Firefox";
                default: return id;
            }
        }

        public static bool IsBrowser(string id)
        {
            return Array.IndexOf(BrowserIds, id) >= 0;
        }

        private static string checksum;

        /// <summary>"v0.4.0 - 1a2b3c4d": the version and the first 8 characters of the program file's checksum.</summary>
        public static string VersionTag
        {
            get
            {
                if (checksum == null)
                {
                    checksum = "";
                    try
                    {
                        using (var sha = System.Security.Cryptography.SHA256.Create())
                        using (var stream = File.OpenRead(System.Reflection.Assembly.GetExecutingAssembly().Location))
                        {
                            checksum = BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", "").Substring(0, 8).ToLowerInvariant();
                        }
                    }
                    catch (Exception)
                    {
                        // The label is cosmetic; show the version alone.
                    }
                }
                return "v" + AppVersion + (checksum.Length > 0 ? " - " + checksum : "");
            }
        }

        /// <summary>BOOKMARKSYNC_DIR lets the tests point everything at a scratch folder.</summary>
        public static string DataDir
        {
            get
            {
                string over = Environment.GetEnvironmentVariable("BOOKMARKSYNC_DIR");
                if (!string.IsNullOrEmpty(over)) return over;
                return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BookmarkSync");
            }
        }

        public static string StateFile { get { return Path.Combine(DataDir, "state.json"); } }
        public static string StatusFile { get { return Path.Combine(DataDir, "status.json"); } }
        public static string ConfigFile { get { return Path.Combine(DataDir, "config.json"); } }
        public static string DefaultBackupDir { get { return Path.Combine(DataDir, "Backups"); } }

        /// <summary>The chosen backup folder, or the default one inside the app folder.</summary>
        public static string BackupDir
        {
            get
            {
                string chosen = Config.Load().BackupFolder;
                return string.IsNullOrEmpty(chosen) ? DefaultBackupDir : chosen;
            }
        }
        public static string PlanDir { get { return Path.Combine(DataDir, "Plans"); } }
        public static string LogPath { get { return Path.Combine(DataDir, "BookmarkSync.log"); } }
        public static string InstalledExe { get { return Path.Combine(DataDir, "BookmarkSync.exe"); } }

        private static readonly UTF8Encoding Utf8NoBom = new UTF8Encoding(false);

        /// <summary>Writes a file by writing a temporary copy, reading it back, then moving it into place.</summary>
        public static void WriteFileSafely(string path, string text)
        {
            string dir = Path.GetDirectoryName(path);
            if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
            string temp = path + "." + Guid.NewGuid().ToString("N").Substring(0, 8) + ".tmp";
            File.WriteAllText(temp, text, Utf8NoBom);
            if (File.ReadAllText(temp, Utf8NoBom) != text)
            {
                File.Delete(temp);
                throw new IOException("The file could not be written correctly: " + path);
            }
            if (File.Exists(path)) File.Delete(path);
            File.Move(temp, path);
        }

        public static string ReadText(string path)
        {
            return File.ReadAllText(path, Utf8NoBom);
        }

        private static readonly object LogLock = new object();

        /// <summary>Appends a line to the log. Logging never stops a sync, so every failure is swallowed.</summary>
        public static void Log(string message)
        {
            try
            {
                lock (LogLock)
                {
                    if (!Directory.Exists(DataDir)) Directory.CreateDirectory(DataDir);
                    File.AppendAllText(LogPath, "[" + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "] " + message + Environment.NewLine, Utf8NoBom);
                    var info = new FileInfo(LogPath);
                    if (info.Length > 400000) TrimLog();
                }
            }
            catch
            {
                // Intentionally ignored.
            }
        }

        private static void TrimLog()
        {
            string[] lines = File.ReadAllLines(LogPath, Utf8NoBom);
            if (lines.Length <= 1000) return;
            var keep = new string[1000];
            Array.Copy(lines, lines.Length - 1000, keep, 0, 1000);
            File.WriteAllLines(LogPath, keep, Utf8NoBom);
        }

        public static double UnixTime()
        {
            return Math.Floor((DateTime.UtcNow - new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalSeconds);
        }

        /// <summary>
        /// Runs an action while holding a lock shared by every copy of the program, so two browsers
        /// syncing at once, or the window and a browser, never write the state over each other.
        /// </summary>
        public static T WithStateLock<T>(Func<T> action)
        {
            using (var mutex = new Mutex(false, "BookmarkSyncState"))
            {
                bool held;
                try
                {
                    held = mutex.WaitOne(30000);
                }
                catch (AbandonedMutexException)
                {
                    // The last holder exited without letting go. The lock is ours now.
                    held = true;
                }
                if (!held) throw new TimeoutException("Bookmark Sync is busy with another browser, try again in a moment.");
                try
                {
                    return action();
                }
                finally
                {
                    mutex.ReleaseMutex();
                }
            }
        }

        public static void WithStateLock(Action action)
        {
            WithStateLock<bool>(() => { action(); return true; });
        }
    }
}
