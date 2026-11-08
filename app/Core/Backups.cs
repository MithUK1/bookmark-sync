using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace BookmarkSync
{
    public sealed class BackupInfo
    {
        public string Name;
        public DateTime When;
        public string Browser;
        public string Type;
    }

    /// <summary>
    /// Per-browser backups. Kinds: change (a browser is about to be changed), daily, weekly, monthly,
    /// yearly, manual and before-restore. Kept per browser: 7 daily, 4 weekly, 12 monthly, 5 yearly. Each is a .json (restore it here) and a .html (import into any browser).
    /// </summary>
    public static class Backups
    {
        private static readonly Regex NamePattern = new Regex(@"^bookmarks-(\d{8})-(\d{6})\d{0,3}-(?:(chrome|edge|firefox|all)-)?(.+)$");

        /// <summary>Saves one browser's bookmarks (or, with browser "all", the master list).</summary>
        public static string Create(State state, string kind, string browser, Snap snapshot)
        {
            if (!Directory.Exists(Env.BackupDir)) Directory.CreateDirectory(Env.BackupDir);
            Snap content = browser == "all" ? state.Master : (snapshot ?? state.Browsers[browser].Snapshot);

            // Milliseconds keep two backups in the same second apart.
            string stamp = DateTime.Now.ToString("yyyyMMdd-HHmmssfff");
            string name = "bookmarks-" + stamp + "-" + browser + "-" + kind;
            var payload = new Dictionary<string, object>();
            payload["version"] = 2;
            payload["created"] = stamp;
            payload["kind"] = kind;
            payload["browser"] = browser;
            payload["master"] = content.ToJson();
            Env.WriteFileSafely(Path.Combine(Env.BackupDir, name + ".json"), Json.Write(payload));
            Env.WriteFileSafely(Path.Combine(Env.BackupDir, name + ".html"), ToHtml(content.Bookmarks));
            Env.Log("Backup saved: " + name);
            return name;
        }

        /// <summary>A fingerprint of a browser's bookmarks and order, to avoid saving the same backup twice.</summary>
        public static string Hash(Snap snapshot)
        {
            var keys = BMap.From(snapshot.Bookmarks).Keys.OrderBy(k => k, StringComparer.Ordinal).ToList();
            var sb = new StringBuilder(string.Join(Env.KeySep.ToString(), keys));
            sb.Append(Json.Write(snapshot.ToJson()["order"]));
            using (var sha = SHA1.Create())
            {
                return BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(sb.ToString())));
            }
        }

        /// <summary>
        /// Takes the backups due for one browser, from what it has right now (so before a sync changes
        /// it): the first of each day, month and year, and one whenever it is about to be changed and
        /// differs from its last backup. The daily one is taken at the first sync after midnight, which
        /// is the next time the browser is open if the computer was off.
        /// </summary>
        public static void Scheduled(State state, string browser, Snap snapshot, bool changing, Config config)
        {
            if (snapshot.Bookmarks.Count == 0) return;
            BackupMark marks;
            if (!state.BackupMarks.TryGetValue(browser, out marks))
            {
                marks = new BackupMark();
                state.BackupMarks[browser] = marks;
            }
            DateTime now = DateTime.Now;
            string day = now.ToString("yyyyMMdd"), month = now.ToString("yyyyMM"), year = now.ToString("yyyy");
            string week = IsoWeek(now);
            try
            {
                if (marks.Daily != day)
                {
                    Create(state, "daily", browser, snapshot);
                    marks.Daily = day;
                    marks.Hash = Hash(snapshot);
                    if (marks.Weekly != week)
                    {
                        Create(state, "weekly", browser, snapshot);
                        marks.Weekly = week;
                    }
                    if (marks.Monthly != month)
                    {
                        Create(state, "monthly", browser, snapshot);
                        marks.Monthly = month;
                    }
                    if (marks.Yearly != year)
                    {
                        Create(state, "yearly", browser, snapshot);
                        marks.Yearly = year;
                    }
                }
                else if (changing)
                {
                    string hash = Hash(snapshot);
                    if (marks.Hash != hash)
                    {
                        Create(state, "change", browser, snapshot);
                        marks.Hash = hash;
                    }
                }
                RemoveOld(config);
            }
            catch (Exception ex)
            {
                // A backup problem must not stop the sync, but it must be visible.
                Env.Log("Backup failed: " + ex.Message);
            }
        }

        /// <summary>The ISO year and week, so the week of 31 December can belong to the next year.</summary>
        public static string IsoWeek(DateTime date)
        {
            DateTime thursday = date.AddDays(3 - ((int)date.DayOfWeek + 6) % 7);
            int week = (thursday.DayOfYear - 1) / 7 + 1;
            return thursday.Year.ToString("0000") + "W" + week.ToString("00");
        }

        /// <summary>
        /// Removes backups past their keep rules, per browser: change backups beyond the number kept,
        /// then the newest 7 daily, 4 weekly, 12 monthly and 5 yearly. Manual and before-restore
        /// backups are never removed.
        /// </summary>
        public static void RemoveOld(Config config)
        {
            if (!Directory.Exists(Env.BackupDir)) return;
            var rows = new List<Tuple<string, DateTime, string, string>>();
            foreach (string file in Directory.GetFiles(Env.BackupDir, "bookmarks-*.json"))
            {
                string baseName = Path.GetFileNameWithoutExtension(file);
                Match m = NamePattern.Match(baseName);
                if (!m.Success) continue;
                DateTime date = DateTime.ParseExact(m.Groups[1].Value, "yyyyMMdd", null);
                string browser = m.Groups[3].Success ? m.Groups[3].Value : "all";
                rows.Add(Tuple.Create(file, date, browser, m.Groups[4].Value));
            }

            var doomed = new List<string>();
            foreach (var group in rows.Where(r => r.Item4 == "change" || r.Item4 == "auto").GroupBy(r => r.Item3))
            {
                doomed.AddRange(group.OrderByDescending(r => Path.GetFileName(r.Item1), StringComparer.Ordinal).Skip(config.AutoBackupsToKeep).Select(r => r.Item1));
            }
            var limits = new Dictionary<string, int>
            {
                { "daily", config.DailiesToKeep }, { "weekly", config.WeekliesToKeep },
                { "monthly", config.MonthlyToKeep }, { "yearly", config.YearliesToKeep }
            };
            foreach (var limit in limits)
            {
                foreach (var group in rows.Where(r => r.Item4 == limit.Key).GroupBy(r => r.Item3))
                {
                    doomed.AddRange(group.OrderByDescending(r => Path.GetFileName(r.Item1), StringComparer.Ordinal).Skip(limit.Value).Select(r => r.Item1));
                }
            }
            foreach (string file in doomed.Distinct())
            {
                try { File.Delete(file); } catch (IOException) { }
                try { File.Delete(Path.ChangeExtension(file, ".html")); } catch (IOException) { }
            }
        }

        /// <summary>Backups newest first, with a readable date, browser and kind.</summary>
        public static List<BackupInfo> List()
        {
            var result = new List<BackupInfo>();
            if (!Directory.Exists(Env.BackupDir)) return result;
            foreach (string file in Directory.GetFiles(Env.BackupDir, "bookmarks-*.json").OrderByDescending(f => Path.GetFileName(f), StringComparer.Ordinal))
            {
                string baseName = Path.GetFileNameWithoutExtension(file);
                Match m = NamePattern.Match(baseName);
                if (!m.Success) continue;
                DateTime when = DateTime.ParseExact(m.Groups[1].Value + m.Groups[2].Value, "yyyyMMddHHmmss", null);
                string browser = "All browsers";
                if (m.Groups[3].Success && Env.IsBrowser(m.Groups[3].Value)) browser = Env.BrowserName(m.Groups[3].Value);
                string kind = m.Groups[4].Value, type;
                switch (kind)
                {
                    case "change": type = "Before a change"; break;
                    case "daily": type = "Daily"; break;
                    case "weekly": type = "Weekly"; break;
                    case "monthly": type = "Monthly"; break;
                    case "yearly": type = "Yearly"; break;
                    case "manual": type = "Manual"; break;
                    case "before-restore": type = "Before restore"; break;
                    case "auto": type = "Automatic (older)"; break;
                    case "before-source": type = "Before use as source"; break;
                    default: type = kind; break;
                }
                result.Add(new BackupInfo { Name = baseName, When = when, Browser = browser, Type = type });
            }
            return result;
        }

        /// <summary>
        /// Makes a backup the master list again. The current list is backed up first, and every
        /// browser is set to change to match on its next sync.
        /// </summary>
        public static void Restore(State state, string name)
        {
            string path = Path.Combine(Env.BackupDir, Path.GetFileName(name) + ".json");
            var payload = Json.Obj(Json.Parse(Env.ReadText(path)));
            Snap restored = Snap.FromJson(Json.Get(payload, "master"));
            Create(state, "before-restore", "all", null);
            state.Master = restored;
            state.Revision++;
            state.PendingOverwrite.Clear();
            state.PendingOverwrite.AddRange(state.Browsers.Keys);
            Env.Log("Restored backup " + name + ", master list now " + restored.Bookmarks.Count);
        }

        // ---------------------------------------------------------- html export

        private sealed class Node
        {
            public readonly List<Bm> Items = new List<Bm>();
            public readonly List<KeyValuePair<string, Node>> Subs = new List<KeyValuePair<string, Node>>();

            public Node Sub(string name)
            {
                foreach (var pair in Subs) if (pair.Key == name) return pair.Value;
                var node = new Node();
                Subs.Add(new KeyValuePair<string, Node>(name, node));
                return node;
            }
        }

        /// <summary>The standard bookmarks .html file every browser can import.</summary>
        public static string ToHtml(IEnumerable<Bm> bookmarks)
        {
            var tree = new Node();
            foreach (Bm bm in bookmarks)
            {
                Node node = tree;
                foreach (string part in bm.Path) node = node.Sub(part);
                node.Items.Add(bm);
            }
            var lines = new List<string>
            {
                "<!DOCTYPE NETSCAPE-Bookmark-file-1>",
                "<META HTTP-EQUIV=\"Content-Type\" CONTENT=\"text/html; charset=UTF-8\">",
                "<TITLE>Bookmarks</TITLE>",
                "<H1>Bookmarks</H1>",
                "<DL><p>"
            };
            AddNode(tree, 1, lines);
            lines.Add("</DL><p>");
            return string.Join("\n", lines);
        }

        private static void AddNode(Node node, int depth, List<string> lines)
        {
            string pad = new string(' ', 4 * depth);
            foreach (Bm bm in node.Items)
            {
                string title = string.IsNullOrEmpty(bm.Title) ? bm.Url : bm.Title;
                lines.Add(pad + "<DT><A HREF=\"" + WebUtility.HtmlEncode(bm.Url) + "\">" + WebUtility.HtmlEncode(title) + "</A>");
            }
            foreach (var pair in node.Subs)
            {
                string attribute = "", title = pair.Key;
                if (depth == 1)
                {
                    if (pair.Key == "toolbar") { attribute = " PERSONAL_TOOLBAR_FOLDER=\"true\""; title = "Bookmarks bar"; }
                    else if (pair.Key == "other") title = "Other bookmarks";
                }
                lines.Add(pad + "<DT><H3" + attribute + ">" + WebUtility.HtmlEncode(title) + "</H3>");
                lines.Add(pad + "<DL><p>");
                AddNode(pair.Value, depth + 1, lines);
                lines.Add(pad + "</DL><p>");
            }
        }
    }
}
