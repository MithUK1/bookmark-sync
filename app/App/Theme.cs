using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace BookmarkSync
{
    /// <summary>The look shared by every window, the same as the other tools (Monitor Switcher is the standard).</summary>
    public static class Theme
    {
        public static readonly Color WindowBg = Color.FromArgb(231, 234, 239);
        public static readonly Color CardBg = Color.White;
        public static readonly Color Border = Color.FromArgb(188, 194, 202);
        public static readonly Color Text = Color.FromArgb(31, 35, 40);
        public static readonly Color Muted = Color.FromArgb(107, 114, 128);
        public static readonly Color Accent = Color.FromArgb(43, 108, 223);
        public static readonly Color AccentDark = Color.FromArgb(27, 79, 173);
        public static readonly Color OkFg = Color.FromArgb(15, 110, 75);
        public static readonly Color WarningFg = Color.FromArgb(180, 83, 9);
        public static readonly Color ErrorFg = Color.FromArgb(180, 0, 0);

        public static readonly Font UiFont = new Font("Segoe UI", 9.5f);
        public static readonly Font UiFontBold = new Font("Segoe UI", 9.5f, FontStyle.Bold);
        public static readonly Font UiFontSmall = new Font("Segoe UI", 8.5f);

        private static Icon icon;

        /// <summary>A bookmark ribbon with two arrows chasing each other, drawn in code.</summary>
        public static Icon AppIcon
        {
            get
            {
                if (icon != null) return icon;
                using (var bitmap = new Bitmap(32, 32))
                using (var g = Graphics.FromImage(bitmap))
                {
                    g.SmoothingMode = SmoothingMode.AntiAlias;
                    g.Clear(Color.Transparent);
                    using (var square = new GraphicsPath())
                    {
                        square.AddArc(1, 1, 10, 10, 180, 90);
                        square.AddArc(21, 1, 10, 10, 270, 90);
                        square.AddArc(21, 21, 10, 10, 0, 90);
                        square.AddArc(1, 21, 10, 10, 90, 90);
                        square.CloseFigure();
                        using (var brush = new SolidBrush(Accent)) g.FillPath(brush, square);
                    }
                    g.FillPolygon(Brushes.White, new[] { new Point(10, 6), new Point(22, 6), new Point(22, 26), new Point(16, 21), new Point(10, 26) });
                    using (var pen = new Pen(AccentDark, 1.6f))
                    {
                        g.DrawArc(pen, 12, 9, 8, 8, 200, 140);
                        g.DrawLine(pen, 19, 10, 20, 13);
                        g.DrawArc(pen, 12, 9, 8, 8, 20, 140);
                        g.DrawLine(pen, 13, 16, 12, 13);
                    }
                    icon = Icon.FromHandle(bitmap.GetHicon());
                }
                return icon;
            }
        }

        /// <summary>Background, font, icon, the slim accent bar across the top and the version label bottom left.</summary>
        public static void Chrome(Form form)
        {
            form.BackColor = WindowBg;
            form.Font = UiFont;
            form.Icon = AppIcon;
            form.FormBorderStyle = FormBorderStyle.FixedDialog;
            form.MaximizeBox = false;
            form.MinimizeBox = false;
            form.KeyPreview = true;

            var bar = new Panel { Height = 4, Dock = DockStyle.Top, BackColor = Accent };
            form.Controls.Add(bar);
            var version = new Label
            {
                Text = Env.VersionTag,
                Font = UiFontSmall,
                ForeColor = Muted,
                AutoSize = true,
                Location = new Point(16, form.ClientSize.Height - 22),
                Anchor = AnchorStyles.Bottom | AnchorStyles.Left
            };
            form.Controls.Add(version);
        }

        /// <summary>Flat buttons. One primary action per window, warning only for actions that remove or replace something.</summary>
        public static Button Button(string text, int x, int y, int width = 110, bool primary = false, bool warning = false)
        {
            var button = new Button
            {
                Text = text,
                Location = new Point(x, y),
                Size = new Size(width, 30),
                FlatStyle = FlatStyle.Flat,
                Font = UiFont,
                Cursor = Cursors.Hand
            };
            button.FlatAppearance.BorderSize = 1;
            if (primary)
            {
                button.BackColor = Accent;
                button.ForeColor = Color.White;
                button.FlatAppearance.BorderColor = Accent;
                button.FlatAppearance.MouseOverBackColor = AccentDark;
                button.FlatAppearance.MouseDownBackColor = AccentDark;
            }
            else if (warning)
            {
                button.BackColor = CardBg;
                button.ForeColor = WarningFg;
                button.FlatAppearance.BorderColor = WarningFg;
                button.FlatAppearance.MouseOverBackColor = Color.FromArgb(255, 247, 237);
            }
            else
            {
                button.BackColor = CardBg;
                button.ForeColor = Text;
                button.FlatAppearance.BorderColor = Border;
                button.FlatAppearance.MouseOverBackColor = WindowBg;
            }
            return button;
        }

        public static Label Heading(string text, int x, int y)
        {
            return new Label { Text = text, Font = UiFontBold, ForeColor = Text, AutoSize = true, Location = new Point(x, y) };
        }

        public static Label SmallPrint(string text, int x, int y, int width, int height = 32)
        {
            return new Label { Text = text, Font = UiFontSmall, ForeColor = Muted, Location = new Point(x, y), Size = new Size(width, height) };
        }

        /// <summary>A white card holding a list, with columns and full-row selection.</summary>
        public static ListView CardList(int x, int y, int width, int height, params object[][] columns)
        {
            var list = new ListView
            {
                Location = new Point(x, y),
                Size = new Size(width, height),
                View = View.Details,
                FullRowSelect = true,
                MultiSelect = false,
                HideSelection = false,
                BorderStyle = BorderStyle.FixedSingle,
                BackColor = CardBg,
                HeaderStyle = ColumnHeaderStyle.Nonclickable
            };
            foreach (object[] column in columns) list.Columns.Add((string)column[0], (int)column[1]);
            return list;
        }

        public static bool Confirm(string message, string title)
        {
            return MessageBox.Show(message, title, MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes;
        }

        public static void Error(string message)
        {
            MessageBox.Show(message, Env.AppTitle, MessageBoxButtons.OK, MessageBoxIcon.Error);
        }

        public static string FormatTime(double unix)
        {
            if (unix <= 0) return "never";
            DateTime when = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddSeconds(unix).ToLocalTime();
            return when.Date == DateTime.Now.Date ? when.ToString("HH:mm") : when.ToString("d MMM HH:mm");
        }
    }
}
