using System;
using System.Collections.Generic;
using System.Linq;

namespace BookmarkSync
{
    public sealed class SyncResult
    {
        /// <summary>The browser already matches the master list; nothing to apply.</summary>
        public bool Unchanged;
        /// <summary>The browser follows a master browser, so titles are copied too.</summary>
        public bool Mirror;
    }

    /// <summary>
    /// The merge: how each browser's bookmarks, folders and order become the master list, and what
    /// each browser must change to match it.
    /// </summary>
    public static class Engine
    {
        // ------------------------------------------------------------------ maps

        public static Dictionary<string, string[]> FolderMap(IEnumerable<string[]> folders)
        {
            var map = new Dictionary<string, string[]>();
            if (folders == null) return map;
            foreach (string[] path in folders)
            {
                if (path.Length > 0) map[string.Join(Env.PathSep.ToString(), path)] = path;
            }
            return map;
        }

        public static Dictionary<string, List<string>> OrderMap(IEnumerable<OrderEntry> order)
        {
            var map = new Dictionary<string, List<string>>();
            if (order == null) return map;
            foreach (OrderEntry entry in order)
            {
                if (entry.Path.Length > 0) map[entry.Key] = new List<string>(entry.Items);
            }
            return map;
        }

        public static List<OrderEntry> OrderList(Dictionary<string, List<string>> map)
        {
            var list = new List<OrderEntry>();
            foreach (string key in map.Keys.OrderBy(k => k, StringComparer.Ordinal))
            {
                list.Add(new OrderEntry { Path = key.Split(Env.PathSep), Items = new List<string>(map[key]) });
            }
            return list;
        }

        public static List<string[]> SortedFolders(Dictionary<string, string[]> folders)
        {
            return folders.Keys.OrderBy(k => k, StringComparer.Ordinal).Select(k => folders[k]).ToList();
        }

        private static bool SameKeys(IEnumerable<string> first, IEnumerable<string> second)
        {
            var a = new HashSet<string>(first);
            var b = new HashSet<string>(second);
            return a.Count == b.Count && a.SetEquals(b);
        }

        private static bool SameSequence(IList<string> a, IList<string> b)
        {
            if (a.Count != b.Count) return false;
            for (int i = 0; i < a.Count; i++) if (!string.Equals(a[i], b[i], StringComparison.Ordinal)) return false;
            return true;
        }

        // ------------------------------------------------------------ duplicates

        /// <summary>Keeps one copy of each address, in the best location. Ties keep the first one seen.</summary>
        public static BMap SelectUnique(BMap map)
        {
            var best = new Dictionary<string, string>();
            foreach (string key in map.Keys)
            {
                string urlKey = Rules.NormalUrl(map[key].Url);
                string current;
                if (!best.TryGetValue(urlKey, out current) || Rules.LocationRank(map[key]) < Rules.LocationRank(map[current])) best[urlKey] = key;
            }
            var keep = new HashSet<string>(best.Values);
            var result = new BMap();
            foreach (string key in map.Keys) if (keep.Contains(key)) result[key] = map[key];
            return result;
        }

        /// <summary>Makes sure every folder that holds a bookmark is listed, so none is dropped while in use.</summary>
        public static void AddParentFolders(BMap map, Dictionary<string, string[]> folders)
        {
            foreach (Bm bm in map.Values)
            {
                for (int depth = 2; depth <= bm.Path.Length; depth++)
                {
                    string[] path = bm.Path.Take(depth).ToArray();
                    folders[string.Join(Env.PathSep.ToString(), path)] = path;
                }
            }
        }

        // ----------------------------------------------------------------- order

        /// <summary>
        /// Folds one browser's folder order into the master. A folder the browser reordered since its
        /// last sync takes that order; otherwise new items go in after their predecessor.
        /// </summary>
        public static void MergeFolderOrder(Dictionary<string, List<string>> master, Dictionary<string, List<string>> current, Dictionary<string, List<string>> lastOrder)
        {
            foreach (var pair in current)
            {
                string key = pair.Key;
                List<string> cur = pair.Value;
                if (!master.ContainsKey(key))
                {
                    master[key] = new List<string>(cur);
                    continue;
                }
                List<string> m = master[key];
                bool reordered = false;
                if (lastOrder != null && lastOrder.ContainsKey(key))
                {
                    List<string> last = lastOrder[key];
                    var inCurrent = new HashSet<string>(cur);
                    var inLast = new HashSet<string>(last);
                    var keptNow = cur.Where(inLast.Contains).ToList();
                    var keptBefore = last.Where(inCurrent.Contains).ToList();
                    reordered = !SameSequence(keptNow, keptBefore);
                }
                if (reordered)
                {
                    var merged = new List<string>(cur);
                    var inMerged = new HashSet<string>(cur);
                    foreach (string item in m) if (inMerged.Add(item)) merged.Add(item);
                    master[key] = merged;
                }
                else
                {
                    var inMaster = new HashSet<string>(m);
                    for (int i = 0; i < cur.Count; i++)
                    {
                        if (inMaster.Contains(cur[i])) continue;
                        int at = 0;
                        for (int j = i - 1; j >= 0; j--)
                        {
                            int position = m.IndexOf(cur[j]);
                            if (position >= 0) { at = position + 1; break; }
                        }
                        m.Insert(at, cur[i]);
                        inMaster.Add(cur[i]);
                    }
                }
            }
        }

        /// <summary>
        /// Makes the order lists match what is in the master: every folder has one, it holds exactly the
        /// bookmarks and sub-folders in that folder, and anything new goes at the end.
        /// </summary>
        public static void CompleteFolderOrder(Dictionary<string, List<string>> order, BMap map, Dictionary<string, string[]> folders)
        {
            var children = new Dictionary<string, List<string>>();
            children["toolbar"] = new List<string>();
            children["other"] = new List<string>();
            foreach (string key in folders.Keys) if (!children.ContainsKey(key)) children[key] = new List<string>();

            foreach (Bm bm in map.Values.OrderBy(b => b.Index))
            {
                string parent = bm.FolderKey;
                if (!children.ContainsKey(parent)) children[parent] = new List<string>();
                children[parent].Add(Rules.BookmarkToken(bm.Url));
            }
            foreach (string key in folders.Keys.OrderBy(k => k, StringComparer.Ordinal))
            {
                string[] path = folders[key];
                string parent = string.Join(Env.PathSep.ToString(), path.Take(path.Length - 1));
                if (!children.ContainsKey(parent)) children[parent] = new List<string>();
                children[parent].Add(Rules.FolderToken(path[path.Length - 1]));
            }

            foreach (string key in order.Keys.ToList()) if (!children.ContainsKey(key)) order.Remove(key);
            foreach (var pair in children)
            {
                var present = new HashSet<string>(pair.Value);
                var final = new List<string>();
                var placed = new HashSet<string>();
                List<string> existing;
                if (order.TryGetValue(pair.Key, out existing))
                {
                    foreach (string token in existing) if (present.Contains(token) && placed.Add(token)) final.Add(token);
                }
                foreach (string token in pair.Value) if (placed.Add(token)) final.Add(token);
                order[pair.Key] = final;
            }
        }

        /// <summary>True when the browser's order already matches the master's, folder by folder.</summary>
        public static bool OrderMatches(Dictionary<string, List<string>> master, Dictionary<string, List<string>> current)
        {
            foreach (var pair in current)
            {
                List<string> m;
                if (!master.TryGetValue(pair.Key, out m)) continue;
                var inCurrent = new HashSet<string>(pair.Value);
                var wanted = m.Where(inCurrent.Contains).ToList();
                if (!SameSequence(wanted, pair.Value)) return false;
            }
            return true;
        }

        public static bool TitlesMatch(BMap master, BMap current)
        {
            foreach (string key in current.Keys)
            {
                if (master.Contains(key) && !string.Equals(master[key].Title, current[key].Title, StringComparison.Ordinal)) return false;
            }
            return true;
        }

        // ---------------------------------------------------------- master browser

        /// <summary>
        /// The browser the others copy, or null when none is chosen (or, in automatic mode, none has
        /// synced). Automatic picks the first browser in the priority list that has synced, counting
        /// the one syncing now.
        /// </summary>
        public static string MirrorSource(State state, Config config, string current)
        {
            string mode = config.MasterBrowser;
            if (Env.IsBrowser(mode)) return mode;
            if (mode != "auto") return null;
            foreach (string raw in (config.BrowserPriority ?? "").Split(','))
            {
                string candidate = raw.Trim().ToLowerInvariant();
                if (!Env.IsBrowser(candidate)) continue;
                if (candidate == current) return candidate;
                BrowserEntry entry;
                if (state.Browsers.TryGetValue(candidate, out entry) && entry.Last != null) return candidate;
            }
            return null;
        }

        // ------------------------------------------------------------------ sync

        /// <summary>
        /// Merges one browser's bookmarks into the master list and says whether that browser already
        /// matches it. Nothing is removed on a browser's first sync.
        /// </summary>
        public static SyncResult Sync(State state, string browser, Snap snap, Config config)
        {
            BrowserEntry entry = state.Entry(browser);
            BMap current = BMap.From(snap.Bookmarks);
            var currentFolders = FolderMap(snap.Folders);
            var currentOrder = OrderMap(snap.Order);
            entry.Snapshot = new Snap { Bookmarks = current.Values.ToList(), Folders = currentFolders.Values.ToList(), Order = snap.Order };
            entry.Seen = Env.UnixTime();

            string before = Json.Write(state.Master.ToJson());
            BMap master = BMap.From(state.Master.Bookmarks);
            var masterFolders = FolderMap(state.Master.Folders);
            var masterOrder = OrderMap(state.Master.Order);
            string name = Env.BrowserName(browser);
            bool activity = false;
            string note;

            // With a master browser chosen, its bookmarks are the master list and the others only
            // follow. Until the master browser has synced once, the others take part normally.
            string source = MirrorSource(state, config, browser);
            bool isSource = source == browser;
            BrowserEntry sourceEntry;
            bool follows = source != null && !isSource && state.Browsers.TryGetValue(source, out sourceEntry) && sourceEntry.Last != null;

            if (state.PendingOverwrite.Contains(browser))
            {
                // After a restore every browser, the master browser too, takes the master list once.
                state.PendingOverwrite.Remove(browser);
                note = "changed to match the master list";
                activity = true;
            }
            else if (isSource)
            {
                if (current.Count == 0 && master.Count > 0)
                {
                    // An empty profile must not wipe every other browser.
                    note = "sent an empty list, ignored";
                    activity = true;
                }
                else
                {
                    master = new BMap();
                    foreach (string key in current.Keys) master[key] = current[key];
                    masterFolders = new Dictionary<string, string[]>(currentFolders);
                    masterOrder = new Dictionary<string, List<string>>();
                    foreach (var pair in currentOrder) masterOrder[pair.Key] = new List<string>(pair.Value);
                    note = "is the master browser";
                }
            }
            else if (follows)
            {
                note = "follows " + Env.BrowserName(source);
            }
            else
            {
                var last = new HashSet<string>(entry.Last ?? new List<string>());
                var lastFolders = FolderMap(entry.LastFolders);
                Dictionary<string, List<string>> lastOrder = entry.LastOrder == null ? null : OrderMap(entry.LastOrder);

                int added = 0, removed = 0;
                foreach (string key in current.Keys)
                {
                    if (!last.Contains(key))
                    {
                        if (!master.Contains(key)) master[key] = current[key];
                        added++;
                    }
                }
                foreach (string key in last)
                {
                    if (!current.Contains(key))
                    {
                        if (master.Contains(key)) master.Remove(key);
                        removed++;
                    }
                }
                foreach (var pair in currentFolders) if (!lastFolders.ContainsKey(pair.Key)) masterFolders[pair.Key] = pair.Value;
                foreach (string key in lastFolders.Keys) if (!currentFolders.ContainsKey(key)) masterFolders.Remove(key);
                MergeFolderOrder(masterOrder, currentOrder, lastOrder);
                note = added + " added and " + removed + " removed in this browser";
                activity = added > 0 || removed > 0;
            }

            if (config.RemoveDuplicates) master = SelectUnique(master);
            AddParentFolders(master, masterFolders);
            CompleteFolderOrder(masterOrder, master, masterFolders);

            state.Master = new Snap { Bookmarks = master.Values.ToList(), Folders = SortedFolders(masterFolders), Order = OrderList(masterOrder) };
            if (Json.Write(state.Master.ToJson()) != before)
            {
                state.Revision++;
                activity = true;
            }
            if (activity) Env.Log(name + ": sync, " + note + ", master list now " + master.Count);

            bool unchanged = SameKeys(master.Keys, current.Keys)
                && SameKeys(masterFolders.Keys, currentFolders.Keys)
                && OrderMatches(masterOrder, currentOrder);
            if (unchanged && follows) unchanged = TitlesMatch(master, current);

            // Back up this browser as it is now, before anything changes it.
            Backups.Scheduled(state, browser, entry.Snapshot, !unchanged, config);

            if (unchanged)
            {
                entry.Applied = state.Revision;
                entry.Last = current.Keys.ToList();
                entry.LastFolders = currentFolders.Values.ToList();
                entry.LastOrder = snap.Order;
            }
            return new SyncResult { Unchanged = unchanged, Mirror = follows };
        }

        /// <summary>
        /// Records what a browser looks like after it has applied the master list, so its next sync can
        /// tell what the user changed since. Revision is the one it was sent.
        /// </summary>
        public static void Complete(State state, string browser, Snap snap, int revision, int created, int deleted, int moved, int errors)
        {
            BrowserEntry entry = state.Entry(browser);
            BMap current = BMap.From(snap.Bookmarks);
            var folders = FolderMap(snap.Folders);
            entry.Last = current.Keys.ToList();
            entry.LastFolders = folders.Values.ToList();
            entry.LastOrder = snap.Order;
            entry.Applied = revision;
            entry.Snapshot = new Snap { Bookmarks = current.Values.ToList(), Folders = folders.Values.ToList(), Order = snap.Order };
            entry.Seen = Env.UnixTime();
            if (created != 0 || deleted != 0 || moved != 0 || errors != 0)
            {
                Env.Log(Env.BrowserName(browser) + ": applied, " + created + " added, " + deleted + " removed, " + moved + " moved, " + errors + " could not be changed");
            }
        }
    }
}
