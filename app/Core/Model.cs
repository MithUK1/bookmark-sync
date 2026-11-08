using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace BookmarkSync
{
    /// <summary>Bookmarks keyed by folder and address. The first copy of a key wins; order is kept.</summary>
    public sealed class BMap
    {
        public readonly List<string> Keys = new List<string>();
        private readonly Dictionary<string, Bm> items = new Dictionary<string, Bm>();

        public int Count { get { return Keys.Count; } }

        public bool Contains(string key) { return items.ContainsKey(key); }

        public Bm this[string key]
        {
            get { return items[key]; }
            set
            {
                if (!items.ContainsKey(key)) Keys.Add(key);
                items[key] = value;
            }
        }

        public void Remove(string key)
        {
            if (items.Remove(key)) Keys.Remove(key);
        }

        public IEnumerable<Bm> Values { get { foreach (string k in Keys) yield return items[k]; } }

        public static BMap From(IEnumerable<Bm> bookmarks)
        {
            var map = new BMap();
            foreach (Bm bm in bookmarks)
            {
                string key = bm.Key;
                if (!map.Contains(key)) map[key] = bm;
            }
            return map;
        }
    }

    /// <summary>One folder's items in order: bookmarks as b|address, sub-folders as f|name.</summary>
    public sealed class OrderEntry
    {
        public string[] Path = new string[0];
        public List<string> Items = new List<string>();

        public string Key { get { return string.Join(Env.PathSep.ToString(), Path); } }
    }

    /// <summary>A browser's bookmarks, folders and order at one moment.</summary>
    public sealed class Snap
    {
        public List<Bm> Bookmarks = new List<Bm>();
        public List<string[]> Folders = new List<string[]>();
        public List<OrderEntry> Order = new List<OrderEntry>();

        public Dictionary<string, object> ToJson()
        {
            var d = new Dictionary<string, object>();
            d["bookmarks"] = Bookmarks.Select(b => (object)b.ToJson()).ToList();
            d["folders"] = Folders.Select(f => (object)new List<object>(f)).ToList();
            d["order"] = Order.Select(o => (object)new Dictionary<string, object>
            {
                { "path", new List<object>(o.Path) },
                { "items", new List<object>(o.Items) }
            }).ToList();
            return d;
        }

        /// <summary>Reads a snapshot, dropping anything outside the two synced areas or malformed.</summary>
        public static Snap FromJson(object value)
        {
            var snap = new Snap();
            var o = Json.Obj(value);
            if (o == null) return snap;
            var bookmarks = Json.Arr(Json.Get(o, "bookmarks"));
            if (bookmarks != null)
            {
                foreach (object item in bookmarks)
                {
                    Bm bm = Bm.FromJson(item);
                    if (bm != null && bm.Path.Length > 0 && (bm.Path[0] == "toolbar" || bm.Path[0] == "other")) snap.Bookmarks.Add(bm);
                }
            }
            var folders = Json.Arr(Json.Get(o, "folders"));
            if (folders != null)
            {
                foreach (object item in folders)
                {
                    string[] path = Json.StrList(item).ToArray();
                    if (path.Length > 0 && (path[0] == "toolbar" || path[0] == "other")) snap.Folders.Add(path);
                }
            }
            var order = Json.Arr(Json.Get(o, "order"));
            if (order != null)
            {
                foreach (object item in order)
                {
                    string[] path = Json.StrList(Json.Get(item, "path")).ToArray();
                    if (path.Length == 0 || (path[0] != "toolbar" && path[0] != "other")) continue;
                    snap.Order.Add(new OrderEntry { Path = path, Items = Json.StrList(Json.Get(item, "items")) });
                }
            }
            return snap;
        }
    }

    public sealed class BrowserEntry
    {
        /// <summary>Keys the browser had after its last sync; null before its first.</summary>
        public List<string> Last;
        public List<string[]> LastFolders;
        public List<OrderEntry> LastOrder;
        public int Applied;
        public double Seen;
        public Snap Snapshot;
    }

    public sealed class BackupMark
    {
        public string Daily = "";
        public string Weekly = "";
        public string Monthly = "";
        public string Yearly = "";
        public string Hash = "";
    }

    /// <summary>Everything in state.json: the master list, what each browser last had, and bookkeeping.</summary>
    public sealed class State
    {
        public Snap Master = new Snap();
        public int Revision = 1;
        public readonly Dictionary<string, BrowserEntry> Browsers = new Dictionary<string, BrowserEntry>();
        public readonly List<string> PendingOverwrite = new List<string>();
        public readonly Dictionary<string, BackupMark> BackupMarks = new Dictionary<string, BackupMark>();

        public BrowserEntry Entry(string browser)
        {
            BrowserEntry e;
            if (!Browsers.TryGetValue(browser, out e))
            {
                e = new BrowserEntry();
                Browsers[browser] = e;
            }
            return e;
        }

        /// <summary>Loads state.json. A file that will not read is a real fault and is passed up.</summary>
        public static State Load()
        {
            var state = new State();
            if (!File.Exists(Env.StateFile)) return state;
            var raw = Json.Obj(Json.Parse(Env.ReadText(Env.StateFile)));
            if (raw == null) return state;

            state.Master = Snap.FromJson(Json.Get(raw, "master"));
            int rev = Json.Int(Json.Get(raw, "revision"));
            if (rev > 0) state.Revision = rev;

            var browsers = Json.Obj(Json.Get(raw, "browsers"));
            if (browsers != null)
            {
                foreach (var pair in browsers)
                {
                    var e = Json.Obj(pair.Value);
                    if (e == null) continue;
                    var entry = new BrowserEntry();
                    object last = Json.Get(e, "last");
                    if (last != null) entry.Last = Json.StrList(last);
                    var lastFolders = Json.Arr(Json.Get(e, "last_folders"));
                    if (lastFolders != null)
                    {
                        entry.LastFolders = new List<string[]>();
                        foreach (object f in lastFolders) entry.LastFolders.Add(Json.StrList(f).ToArray());
                    }
                    var lastOrder = Json.Arr(Json.Get(e, "last_order"));
                    if (lastOrder != null) entry.LastOrder = Snap.FromJson(new Dictionary<string, object> { { "order", lastOrder } }).Order;
                    entry.Applied = Json.Int(Json.Get(e, "applied"));
                    entry.Seen = Json.Num(Json.Get(e, "seen"));
                    object snap = Json.Get(e, "snapshot");
                    if (snap != null) entry.Snapshot = Snap.FromJson(snap);
                    state.Browsers[pair.Key] = entry;
                }
            }
            state.PendingOverwrite.AddRange(Json.StrList(Json.Get(raw, "pending_overwrite")));

            var marks = Json.Obj(Json.Get(raw, "backup_marks"));
            if (marks != null)
            {
                foreach (var pair in marks)
                {
                    state.BackupMarks[pair.Key] = new BackupMark
                    {
                        Daily = Json.Str(Json.Get(pair.Value, "daily")),
                        Weekly = Json.Str(Json.Get(pair.Value, "weekly")),
                        Monthly = Json.Str(Json.Get(pair.Value, "monthly")),
                        Yearly = Json.Str(Json.Get(pair.Value, "yearly")),
                        Hash = Json.Str(Json.Get(pair.Value, "hash"))
                    };
                }
            }
            return state;
        }

        /// <summary>
        /// Saves state.json, then a small status.json (revision, pending, what each browser has
        /// applied) that quick checks and the live push read without loading the whole state.
        /// status.json is only rewritten when it changed, so watchers are not woken for nothing.
        /// </summary>
        public void Save()
        {
            var root = new Dictionary<string, object>();
            root["master"] = Master.ToJson();
            root["revision"] = Revision;
            var browsers = new Dictionary<string, object>();
            foreach (var pair in Browsers)
            {
                BrowserEntry e = pair.Value;
                var d = new Dictionary<string, object>();
                d["last"] = e.Last == null ? null : e.Last.Select(x => (object)x).ToList();
                d["last_folders"] = e.LastFolders == null ? null : e.LastFolders.Select(f => (object)new List<object>(f)).ToList();
                d["last_order"] = e.LastOrder == null ? null : (object)new Snap { Order = e.LastOrder }.ToJson()["order"];
                d["applied"] = e.Applied;
                d["seen"] = e.Seen;
                d["snapshot"] = e.Snapshot == null ? null : e.Snapshot.ToJson();
                browsers[pair.Key] = d;
            }
            root["browsers"] = browsers;
            root["pending_overwrite"] = PendingOverwrite.Select(x => (object)x).ToList();
            var marks = new Dictionary<string, object>();
            foreach (var pair in BackupMarks)
            {
                marks[pair.Key] = new Dictionary<string, object>
                {
                    { "daily", pair.Value.Daily }, { "weekly", pair.Value.Weekly }, { "monthly", pair.Value.Monthly },
                    { "yearly", pair.Value.Yearly }, { "hash", pair.Value.Hash }
                };
            }
            root["backup_marks"] = marks;
            Env.WriteFileSafely(Env.StateFile, Json.Write(root));

            var applied = new Dictionary<string, object>();
            foreach (var pair in Browsers) applied[pair.Key] = pair.Value.Applied;
            var status = new Dictionary<string, object>
            {
                { "revision", Revision },
                { "pending", PendingOverwrite.Select(x => (object)x).ToList() },
                { "applied", applied }
            };
            string text = Json.Write(status);
            if (!File.Exists(Env.StatusFile) || Env.ReadText(Env.StatusFile) != text) Env.WriteFileSafely(Env.StatusFile, text);
        }
    }

    /// <summary>The small status.json, read by the quick check and the live push.</summary>
    public sealed class Status
    {
        public int Revision;
        public readonly List<string> Pending = new List<string>();
        public readonly Dictionary<string, int> Applied = new Dictionary<string, int>();

        public static Status Load()
        {
            var status = new Status();
            try
            {
                if (!File.Exists(Env.StatusFile)) return status;
                var raw = Json.Obj(Json.Parse(Env.ReadText(Env.StatusFile)));
                if (raw == null) return status;
                status.Revision = Json.Int(Json.Get(raw, "revision"));
                status.Pending.AddRange(Json.StrList(Json.Get(raw, "pending")));
                var applied = Json.Obj(Json.Get(raw, "applied"));
                if (applied != null) foreach (var pair in applied) status.Applied[pair.Key] = Json.Int(pair.Value);
            }
            catch
            {
                // A half-written or missing file simply means "not current".
                status.Revision = -1;
            }
            return status;
        }

        /// <summary>True when the browser has applied the latest master list and nothing is waiting for it.</summary>
        public bool IsCurrent(string browser)
        {
            int applied;
            return Revision >= 0 && Applied.TryGetValue(browser, out applied) && applied == Revision && !Pending.Contains(browser);
        }
    }
}
