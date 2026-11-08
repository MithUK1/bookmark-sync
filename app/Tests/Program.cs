using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace BookmarkSync.Tests
{
    /// <summary>
    /// Tests for the merge, order, master browser, backup and message rules. They load the shipped
    /// Core code and run it against a scratch folder, so nothing touches real bookmarks or settings.
    /// Run with: dotnet run --project app/Tests
    /// </summary>
    internal static class Program
    {
        private static int passes, failures;

        private static void Equal(object expected, object actual, string name)
        {
            if (Equals(expected, actual) || (expected != null && actual != null && expected.ToString() == actual.ToString()))
            {
                passes++;
                return;
            }
            failures++;
            Console.WriteLine("FAIL " + name + "\n  expected: " + expected + "\n  actual:   " + actual);
        }

        private static Bm B(string url, string[] path, string title = "Page", int index = 0)
        {
            return new Bm { Url = url, Title = title, Path = path, Index = index };
        }

        private static string[] P(params string[] parts) { return parts; }

        private static OrderEntry O(string[] path, params string[] items)
        {
            return new OrderEntry { Path = path, Items = items.ToList() };
        }

        private static Snap S(IEnumerable<Bm> bookmarks, IEnumerable<string[]> folders = null, IEnumerable<OrderEntry> order = null)
        {
            return new Snap
            {
                Bookmarks = bookmarks.ToList(),
                Folders = (folders ?? new string[0][]).ToList(),
                Order = (order ?? new OrderEntry[0]).ToList()
            };
        }

        private static string Urls(State s)
        {
            return string.Join(" ", s.Master.Bookmarks.Select(b => b.Url).OrderBy(u => u, StringComparer.Ordinal));
        }

        private static string MasterOrder(State s, string folder)
        {
            List<string> list;
            return Engine.OrderMap(s.Master.Order).TryGetValue(folder, out list) ? string.Join(",", list) : "";
        }

        private static int Count(string pattern)
        {
            return Directory.Exists(Env.BackupDir) ? Directory.GetFiles(Env.BackupDir, pattern).Length : 0;
        }

        private static int Main()
        {
            string scratch = Path.Combine(Path.GetTempPath(), "BookmarkSyncTests-" + Guid.NewGuid().ToString("N"));
            Environment.SetEnvironmentVariable("BOOKMARKSYNC_DIR", scratch);
            var config = new Config();

            // ---- Address rules ----
            Equal("//example.com/page", Rules.NormalUrl("https://www.Example.com/page/"), "www, case and trailing slash ignored");
            Equal("//example.com/page", Rules.NormalUrl("http://example.com:80/page"), "default port ignored");
            Equal("//example.com/a?id=5", Rules.NormalUrl("https://example.com/a?utm_source=x&id=5&fbclid=1#top"), "tracking and fragment removed");
            Equal("//example.com/a", Rules.NormalUrl("https://example.com/a?utm_medium=y"), "query dropped when only tracking");
            Equal("javascript:alert(1)", Rules.NormalUrl(" javascript:alert(1) "), "other schemes compared as typed");
            Equal("//example.com:8080/x", Rules.NormalUrl("https://user@example.com:8080/x"), "login removed, custom port kept");

            // ---- Duplicates ----
            var map = BMap.From(new[] { B("https://site.test/", P("other", "Old")), B("https://www.site.test", P("toolbar")), B("https://site.test/x", P("other")) });
            var unique = Engine.SelectUnique(map);
            Equal(2, unique.Count, "one copy of each address");
            Equal("toolbar", string.Join("/", unique.Values.First().Path), "bookmarks bar copy wins");

            // ---- First sync of two browsers merges both, removes nothing ----
            var state = new State();
            var chrome = S(new[] { B("https://a.test", P("toolbar")), B("https://b.test", P("other", "Work")) }, new[] { P("other", "Work") });
            var result = Engine.Sync(state, "chrome", chrome, config);
            Equal(false, result.Unchanged == false && state.Master.Bookmarks.Count == 0, "first browser fills the master");
            Equal("https://a.test https://b.test", Urls(state), "first browser fills the master list");
            Equal(1, Count("*-daily.json") > 0 ? 1 : 0, "a daily backup on a browser's first sync");

            var edge = S(new[] { B("https://c.test", P("toolbar")) });
            result = Engine.Sync(state, "edge", edge, config);
            Equal(false, result.Unchanged, "second browser needs changes");
            Equal("https://a.test https://b.test https://c.test", Urls(state), "second browser adds, nothing removed");
            Equal(2, Count("*-daily.json"), "a daily backup for each browser");
            Equal(2, Count("*-weekly.json"), "a weekly backup for each browser");
            Equal(2, Count("*-monthly.json"), "a monthly backup for each browser");
            Equal(2, Count("*-yearly.json"), "a yearly backup for each browser");

            // Edge applies the master and reports back.
            Engine.Complete(state, "edge", S(state.Master.Bookmarks, state.Master.Folders, state.Master.Order), state.Revision, 2, 0, 0, 0);

            // ---- A delete in one browser reaches the master ----
            var afterDelete = S(state.Master.Bookmarks.Where(b => b.Url != "https://a.test"), state.Master.Folders, state.Master.Order);
            Engine.Sync(state, "edge", afterDelete, config);
            Equal("https://b.test https://c.test", Urls(state), "delete in edge removes from master");
            Engine.Sync(state, "chrome", chrome, config);
            Equal("https://b.test https://c.test", Urls(state), "old copy in chrome does not bring a delete back");

            // ---- State file round trip ----
            state.Save();
            var loaded = State.Load();
            Equal(Urls(state), Urls(loaded), "state survives save and load");
            Equal("other/Work", string.Join("/", loaded.Master.Folders[0]), "folder paths survive save and load");
            Equal(2, loaded.Browsers["chrome"].Last.Count, "last-seen keys survive save and load");
            Equal(state.Revision, loaded.Revision, "revision survives save and load");
            Equal(true, File.Exists(Env.StatusFile), "the quick check file is written");

            // ---- Backups and restore ----
            string manual = Backups.Create(state, "manual", "all", null);
            string html = File.ReadAllText(Path.Combine(Env.BackupDir, manual + ".html"));
            Equal(true, html.Contains("PERSONAL_TOOLBAR_FOLDER"), "html backup marks the bookmarks bar");
            Equal(true, html.Contains("HREF=\"https://b.test\""), "html backup holds the bookmarks");
            state.Master.Bookmarks.Clear();
            int revisionBefore = state.Revision;
            Backups.Restore(state, manual);
            Equal("https://b.test https://c.test", Urls(state), "restore brings the master back");
            Equal("chrome,edge", string.Join(",", state.PendingOverwrite.OrderBy(x => x)), "restore marks every browser to change");
            Equal(revisionBefore + 1, state.Revision, "restore bumps the revision");
            var listed = Backups.List().First(b => b.Name == manual);
            Equal("Manual", listed.Type, "backup list reads the type");
            Equal("All browsers", listed.Browser, "manual backup covers all browsers");
            Equal("Chrome Edge", string.Join(" ", Backups.List().Where(b => b.Type == "Daily").Select(b => b.Browser).OrderBy(x => x)), "daily backups name their browser");

            // ---- Order and folders ----
            var ord = new State();
            var a = B("https://a.test", P("toolbar"), "A", 0);
            var b2 = B("https://b.test", P("toolbar"), "B", 1);
            var c = B("https://c.test", P("toolbar"), "C", 2);
            var one = S(new[] { a, b2, c }, null, new[] { O(P("toolbar"), "b|//a.test", "b|//b.test", "b|//c.test"), O(P("other")) });
            Engine.Sync(ord, "chrome", one, config);
            Equal("b|//a.test,b|//b.test,b|//c.test", MasterOrder(ord, "toolbar"), "first browser sets the master order");
            Engine.Complete(ord, "chrome", one, ord.Revision, 0, 0, 0, 0);

            var two = S(new[] { c, a, b2 }, null, new[] { O(P("toolbar"), "b|//c.test", "b|//a.test", "b|//b.test"), O(P("other")) });
            result = Engine.Sync(ord, "edge", two, config);
            Equal(false, result.Unchanged, "different order needs changes");
            Equal("b|//a.test,b|//b.test,b|//c.test", MasterOrder(ord, "toolbar"), "first sync of a browser does not change the master order");
            Engine.Complete(ord, "edge", one, ord.Revision, 0, 0, 3, 0);
            Equal(true, Engine.Sync(ord, "edge", one, config).Unchanged, "matching order and revision is unchanged");

            // The user reorders in edge: that order wins and chrome follows.
            var moved = S(new[] { c, a, b2 }, null, new[] { O(P("toolbar"), "b|//c.test", "b|//a.test", "b|//b.test"), O(P("other")) });
            Engine.Sync(ord, "edge", moved, config);
            Equal("b|//c.test,b|//a.test,b|//b.test", MasterOrder(ord, "toolbar"), "a reorder in one browser reaches the master");
            result = Engine.Sync(ord, "chrome", one, config);
            Equal(false, result.Unchanged, "the other browser must follow the new order");
            Equal("b|//c.test,b|//a.test,b|//b.test", MasterOrder(ord, "toolbar"), "following browser does not undo the reorder");

            // A new bookmark lands after the one before it.
            var d = B("https://d.test", P("toolbar"), "D", 1);
            var withNew = S(new[] { c, d, a, b2 }, null, new[] { O(P("toolbar"), "b|//c.test", "b|//d.test", "b|//a.test", "b|//b.test"), O(P("other")) });
            Engine.Complete(ord, "chrome", moved, ord.Revision, 0, 0, 3, 0);
            Engine.Sync(ord, "chrome", withNew, config);
            Equal("b|//c.test,b|//d.test,b|//a.test,b|//b.test", MasterOrder(ord, "toolbar"), "new bookmark is placed after its neighbour");

            // ---- Backups before a change and how many are kept ----
            var bk = new State();
            var bkOne = S(new[] { B("https://a.test", P("toolbar")) });
            var bkTwo = S(new[] { B("https://a.test", P("toolbar")), B("https://z.test", P("toolbar")) });
            var bkThree = S(new[] { B("https://a.test", P("toolbar")), B("https://y.test", P("toolbar")) });
            Engine.Sync(bk, "chrome", bkOne, config);
            Engine.Sync(bk, "edge", bkTwo, config);
            int changeBefore = Backups.List().Count(x => x.Type == "Before a change");
            Engine.Sync(bk, "chrome", bkThree, config);
            Equal(changeBefore + 1, Backups.List().Count(x => x.Type == "Before a change"), "a browser is backed up before a sync changes it");
            Engine.Sync(bk, "chrome", bkThree, config);
            Equal(changeBefore + 1, Backups.List().Count(x => x.Type == "Before a change"), "the same content is not backed up twice");

            foreach (string kind in new[] { "daily", "weekly", "monthly", "yearly" })
            {
                for (int i = 1; i <= 15; i++)
                {
                    string stamp = (2000 + i).ToString() + "0101-120000000";
                    File.WriteAllText(Path.Combine(Env.BackupDir, "bookmarks-" + stamp + "-firefox-" + kind + ".json"), "{}");
                }
            }
            File.WriteAllText(Path.Combine(Env.BackupDir, "bookmarks-20000101-120000000-firefox-manual.json"), "{}");
            Backups.RemoveOld(config);
            Equal(7, Count("bookmarks-20*-firefox-daily.json"), "7 daily backups are kept");
            Equal(4, Count("bookmarks-20*-firefox-weekly.json"), "4 weekly backups are kept");
            Equal(12, Count("bookmarks-20*-firefox-monthly.json"), "12 monthly backups are kept");
            Equal(5, Count("bookmarks-20*-firefox-yearly.json"), "5 yearly backups are kept");
            Equal(1, Count("bookmarks-20000101-*-firefox-manual.json"), "manual backups are kept");
            Equal("2026W44", Backups.IsoWeek(new DateTime(2026, 11, 1)), "ISO week of a Sunday");
            Equal("2020W53", Backups.IsoWeek(new DateTime(2021, 1, 1)), "ISO week at a year boundary");

            // ---- Master browser ----
            var mirror = new Config { MasterBrowser = "firefox" };
            var mb = new State();
            var ff = S(new[] { B("https://a.test", P("toolbar")), B("https://b.test", P("toolbar", "Work")) }, new[] { P("toolbar", "Work") },
                new[] { O(P("toolbar"), "f|Work", "b|//a.test"), O(P("toolbar", "Work"), "b|//b.test"), O(P("other")) });
            var ch = S(new[] { B("https://own.test", P("toolbar")) }, null, new[] { O(P("toolbar"), "b|//own.test"), O(P("other")) });
            Engine.Sync(mb, "chrome", ch, mirror);
            Equal("https://own.test", Urls(mb), "before the master browser has synced, others join normally");
            Engine.Sync(mb, "firefox", ff, mirror);
            Equal("https://a.test https://b.test", Urls(mb), "the master browser replaces the master list");
            result = Engine.Sync(mb, "chrome", ch, mirror);
            Equal(false, result.Unchanged, "a follower is told to match");
            Equal(true, result.Mirror, "a follower is told to copy titles too");
            Equal("https://a.test https://b.test", Urls(mb), "a follower's own bookmarks are not added");

            var ffMoved = S(new[] { B("https://a.test", P("toolbar", "Work"), "Renamed"), B("https://b.test", P("toolbar", "Work")) }, new[] { P("toolbar", "Work") },
                new[] { O(P("toolbar"), "f|Work"), O(P("toolbar", "Work"), "b|//b.test", "b|//a.test"), O(P("other")) });
            Engine.Sync(mb, "firefox", ffMoved, mirror);
            Equal("b|//b.test,b|//a.test", MasterOrder(mb, "toolbar" + Env.PathSep + "Work"), "a move between folders and the new order are copied");
            Equal("Renamed", mb.Master.Bookmarks.First(x => x.Url == "https://a.test").Title, "a rename is copied");
            var stale = S(new[] { B("https://a.test", P("toolbar", "Work"), "Old"), B("https://b.test", P("toolbar", "Work")) }, new[] { P("toolbar", "Work") }, mb.Master.Order);
            Equal(false, Engine.Sync(mb, "edge", stale, mirror).Unchanged, "a follower with an old title is told to change");

            Engine.Sync(mb, "firefox", S(new Bm[0]), mirror);
            Equal("https://a.test https://b.test", Urls(mb), "an empty list from the master browser is ignored");

            var auto = new Config { MasterBrowser = "auto" };
            Equal("firefox", Engine.MirrorSource(mb, auto, "chrome"), "automatic picks the first browser in the list that has synced");
            Equal("chrome", Engine.MirrorSource(new State(), auto, "chrome"), "automatic uses the syncing browser when none has synced");
            Equal("firefox", Engine.MirrorSource(new State(), mirror, "chrome"), "a chosen browser is the source before it has synced");
            Equal(null, Engine.MirrorSource(new State(), new Config(), "chrome"), "no source when off");

            // ---- Quick check file ----
            mb.Save();
            Equal(true, Status.Load().Revision == mb.Revision, "the quick check file holds the revision");
            Equal(true, Status.Load().IsCurrent("firefox"), "an up to date browser is current");
            Equal(false, Status.Load().IsCurrent("opera"), "an unknown browser is not current");

            // ---- Messages ----
            var stream = new MemoryStream();
            string accented = "Café ✓";
            Host.WriteMessage(stream, new Dictionary<string, object> { { "text", accented } });
            stream.Position = 0;
            var back = Json.Obj(Json.Parse(Host.ReadMessage(stream)));
            Equal(accented, Json.Str(Json.Get(back, "text")), "message survives length-prefixed framing");

            string longText = new string('é', 900) + new string('x', 1500);
            var parts = Host.SplitParts(longText, 1000);
            var joined = new List<byte>();
            foreach (string part in parts) joined.AddRange(Convert.FromBase64String(part));
            Equal(4, parts.Count, "large reply split into pieces");
            Equal(longText, new System.Text.UTF8Encoding(false).GetString(joined.ToArray()), "pieces join back to the same text");

            // Whole host requests, as a browser would send them.
            var many = new List<object>();
            for (int i = 0; i < 3000; i++)
            {
                many.Add(new Dictionary<string, object> { { "url", "https://site" + i + ".test/p?id=" + i }, { "title", "T" + i }, { "path", new List<object> { "other", "F" + i % 7 } }, { "index", i } });
            }
            var req = new Dictionary<string, object> { { "type", "sync" }, { "browser", "chrome" }, { "bookmarks", many }, { "folders", new List<object>() }, { "order", new List<object>() } };
            Env.WithStateLock(() => { });
            File.Delete(Env.StateFile);
            var first = Host.Handle(req);
            Equal(true, Json.Bool(Json.Get(first, "ok")), "host sync answers ok");
            var req2 = new Dictionary<string, object> { { "type", "sync" }, { "browser", "edge" }, { "bookmarks", new List<object>() }, { "folders", new List<object>() }, { "order", new List<object>() } };
            var second = Host.Handle(req2);
            Equal(false, Json.Bool(Json.Get(second, "unchanged")), "a second browser is sent the master list");
            var pieces = new List<string> { Json.Str(Json.Get(second, "data")) };
            for (int i = 1; i < Json.Int(Json.Get(second, "parts")); i++)
            {
                pieces.Add(Json.Str(Json.Get(Host.Handle(new Dictionary<string, object> { { "type", "part" }, { "browser", "edge" }, { "part", i } }), "data")));
            }
            var bytes = new List<byte>();
            foreach (string piece in pieces) bytes.AddRange(Convert.FromBase64String(piece));
            var master = Json.Obj(Json.Parse(new System.Text.UTF8Encoding(false).GetString(bytes.ToArray())));
            Equal(3000, Json.Arr(Json.Get(master, "bookmarks")).Count, "the master list arrives whole in pieces");
            Equal(false, Json.Bool(Json.Get(Host.Handle(new Dictionary<string, object> { { "type", "check" }, { "browser", "edge" } }), "current")), "edge is not current before it applies");
            Host.Handle(new Dictionary<string, object> { { "type", "ack" }, { "browser", "edge" }, { "bookmarks", Json.Get(master, "bookmarks") }, { "folders", Json.Get(master, "folders") }, { "order", Json.Get(master, "order") }, { "revision", Json.Get(second, "revision") }, { "stats", new Dictionary<string, object>() } });
            Equal(true, Json.Bool(Json.Get(Host.Handle(new Dictionary<string, object> { { "type", "check" }, { "browser", "edge" } }), "current")), "edge is current after it applies");
            try { Host.Handle(new Dictionary<string, object> { { "type", "sync" }, { "browser", "opera" } }); Equal("throws", "no", "unknown browser is refused"); }
            catch (InvalidOperationException) { passes++; }

            // ---- Settings ----
            File.WriteAllText(Env.ConfigFile, "{ \"RemoveDuplicates\": false, \"AutoBackupsToKeep\": 2");
            var broken = Config.Load();
            Equal(true, broken.Problem != null, "broken settings file is reported");
            Equal(true, broken.RemoveDuplicates, "broken settings file falls back to defaults");
            File.WriteAllText(Env.ConfigFile, "{ \"RemoveDuplicates\": false, \"AutoBackupsToKeep\": 2, \"MasterBrowser\": \"Chrome\" }");
            var read = Config.Load();
            Equal(false, read.RemoveDuplicates, "saved setting is read");
            Equal(5, read.AutoBackupsToKeep, "backups kept never drops below 5");
            Equal("chrome", read.MasterBrowser, "master browser is read, any case");
            read.MasterBrowser = "bogus";
            read.Save();
            Equal("off", Config.Load().MasterBrowser, "an unknown master browser becomes off");

            try { Directory.Delete(scratch, true); } catch (IOException) { }
            Console.WriteLine(passes + " passed, " + failures + " failed");
            return failures > 0 ? 1 : 0;
        }
    }
}
