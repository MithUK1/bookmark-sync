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
}
