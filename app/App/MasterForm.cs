using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;

namespace BookmarkSync
{
    /// <summary>The master list as a tree: the same folders and order every browser is kept to, with a filter box.</summary>
    public sealed class MasterForm : Form
    {
        private readonly Dictionary<string, List<string>> order;
        private readonly Dictionary<string, Bm> byToken = new Dictionary<string, Bm>();
        private readonly List<string> roots;
        private readonly TextBox filter = new TextBox();
        private readonly TreeView tree = new TreeView();

        public MasterForm()
        {
            Text = Env.AppTitle + " - Master list";
            ClientSize = new Size(600, 560);
            StartPosition = FormStartPosition.CenterParent;
            Theme.Chrome(this);

            State state = State.Load();
            order = Engine.OrderMap(state.Master.Order);
            foreach (Bm bm in state.Master.Bookmarks) byToken[bm.FolderKey + Env.KeySep + Rules.BookmarkToken(bm.Url)] = bm;
            roots = order.Keys.Where(k => k.IndexOf(Env.PathSep) < 0).OrderBy(k => k, StringComparer.Ordinal).ToList();

            Controls.Add(new Label
            {
                AutoSize = true, ForeColor = Theme.Muted, Location = new Point(16, 20),
                Text = state.Master.Bookmarks.Count + " bookmarks in " + state.Master.Folders.Count + " folders"
            });
            filter.Location = new Point(16, 46);
            filter.Width = 568;
            filter.TextChanged += (s, e) => Fill();
            Controls.Add(filter);

            tree.Location = new Point(16, 78);
            tree.Size = new Size(568, 410);
            tree.ShowNodeToolTips = true;
            Controls.Add(tree);
            Controls.Add(Theme.SmallPrint("Read only. Hover a bookmark to see its address. Type above to filter.", 16, 496, 568));

            var close = Theme.Button("Close", 494, 520);
            close.Click += (s, e) => Close();
            Controls.Add(close);
            CancelButton = close;
            Fill();
        }

        private void Fill()
        {
            string text = filter.Text.Trim();
            tree.BeginUpdate();
            tree.Nodes.Clear();
            foreach (string root in roots)
            {
                string label = root == "toolbar" ? "Bookmarks bar" : root == "other" ? "Other bookmarks" : root;
                var node = new TreeNode(label);
                AddFolder(node.Nodes, root, text);
                tree.Nodes.Add(node);
                if (text.Length > 0) node.ExpandAll(); else node.Expand();
            }
            tree.EndUpdate();
        }

        private void AddFolder(TreeNodeCollection nodes, string folder, string text)
        {
            List<string> items;
            if (!order.TryGetValue(folder, out items)) return;
            foreach (string token in items)
            {
                if (token.StartsWith("f|"))
                {
                    string name = token.Substring(2);
                    var node = new TreeNode(name);
                    AddFolder(node.Nodes, folder + Env.PathSep + name, text);
                    if (text.Length == 0 || node.Nodes.Count > 0) nodes.Add(node);
                }
                else
                {
                    Bm bm;
                    if (!byToken.TryGetValue(folder + Env.KeySep + token, out bm)) continue;
                    string title = string.IsNullOrEmpty(bm.Title) ? bm.Url : bm.Title;
                    if (text.Length > 0
                        && title.IndexOf(text, StringComparison.OrdinalIgnoreCase) < 0
                        && bm.Url.IndexOf(text, StringComparison.OrdinalIgnoreCase) < 0) continue;
                    nodes.Add(new TreeNode(title) { ToolTipText = bm.Url });
                }
            }
        }
    }

    /// <summary>The help text in its own window.</summary>
    public sealed class HelpForm : Form
    {
        public const string HelpText =
@"USING IT
Install the Bookmark Sync extension in each browser you want kept in step (Chrome, Edge, Firefox). Bookmark Sync runs in the tray (the icon near the clock) and starts with Windows. A browser syncs a moment after you add, move or delete a bookmark, and is told straight away when another browser changes. Close the window with Close, it keeps running in the tray. Right-click the tray icon for the menu, Exit stops it.

The first time a browser syncs, everything it has is added to the master list and nothing is removed. After that, adds, deletes, moves and order in any browser reach the others.

The extension's toolbar button shows when it last synced. Sync now does it straight away. A red ! means it could not reach this app: use Settings > Repair install.

UPDATING
Run a newer BookmarkSync.exe. It replaces the installed copy, keeps your settings, bookmarks and backups, and says what changed. Running an older copy changes nothing.

MASTER BROWSER
Settings > Master browser makes one browser the one the others copy. Its adds, deletes, moves between folders, renames and order are copied to the other browsers, and anything changed in the others is put back. Automatic uses the first browser in the priority list (Firefox, Chrome, Edge) that has synced. Off treats every browser equally, merging all their changes. If the master browser sends an empty list it is ignored.

BACKUPS (UNDO)
Each browser has its own backups, labelled by kind: Before a change (just before a sync changes that browser), Daily (the first sync after midnight, so the next time the browser is open if the computer was off), Weekly, Monthly and Yearly (the first of each), and Manual (Back up now, one per browser). Kept per browser: 7 daily, 4 weekly, 12 monthly, 5 yearly, and the last 40 before-change ones. Each backup is a .json file (restore it here) and a .html file (import it into any browser). Restore makes the selected backup the master list again, after backing up the current one, and every browser changes to match.

DUPLICATES
With Remove duplicate bookmarks on, the same address is kept once. A copy on the bookmarks bar wins, then one in a bookmarks bar folder, then other bookmarks. Addresses that differ only by www, a trailing slash or tracking codes count as the same.

LIMITS
With no master browser, a reorder in one browser is used, and edited titles of existing bookmarks are not synced. With a master browser, titles are copied too. The Firefox bookmarks menu and other bookmarks count as one area. Browser pages such as chrome:// and about: are never synced or changed.

SETTINGS
Settings > Open settings file opens config.json. Restore defaults puts the settings back. Repair install rewrites the browser registration and shortcuts. Remove all app data takes Bookmark Sync off this computer (shortcuts included) and sends its folder, backups included, to the Recycle Bin.";

        public HelpForm()
        {
            Text = Env.AppTitle + " - Help";
            ClientSize = new Size(560, 520);
            StartPosition = FormStartPosition.CenterParent;
            Theme.Chrome(this);
            var text = new TextBox
            {
                Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, BorderStyle = BorderStyle.FixedSingle,
                BackColor = Theme.CardBg, Location = new Point(16, 20), Size = new Size(528, 440), Text = HelpText.Replace("\r\n", "\n").Replace("\n", "\r\n")
            };
            text.Select(0, 0);
            Controls.Add(text);
            var close = Theme.Button("Close", 434, 472);
            close.Click += (s, e) => Close();
            Controls.Add(close);
            CancelButton = close;
        }
    }
}
