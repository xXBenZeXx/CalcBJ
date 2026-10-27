using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace CalcBJ.App
{
    /// <summary>
    /// Nested auto-size layout panels re-measure everything above them whenever a child changes, so building or
    /// refreshing a page control by control is quadratic. Containers are created with layout suspended and the
    /// finished tree is laid out once; refreshes that change many controls are batched the same way.
    /// </summary>
    internal static class UiLayout
    {
        static bool IsContainer(Control c)
        {
            return c is TableLayoutPanel || c is FlowLayoutPanel || (c is Panel && !(c is DataGridView));
        }

        public static void SuspendTree(Control c)
        {
            c.SuspendLayout();
            foreach (Control ch in c.Controls) if (IsContainer(ch)) SuspendTree(ch);
        }

        public static void ResumeTree(Control c)
        {
            foreach (Control ch in c.Controls) if (IsContainer(ch)) ResumeTree(ch);
            c.ResumeLayout(false);
        }

        /// <summary>Resumes every container created suspended under root and lays the tree out once.</summary>
        public static void Finish(Control root)
        {
            ResumeTree(root);
            root.PerformLayout();
            ArrangePanel.ArrangeTops(root);
        }

        public static void Batch(Control root, Action action)
        {
            SuspendTree(root);
            try { action(); }
            finally
            {
                ResumeTree(root);
                root.PerformLayout();
                ArrangePanel.ArrangeTops(root);
            }
        }
    }

    /// <summary>
    /// Base of the app's own layout containers. A whole page is measured and placed in one recursive pass, each
    /// control measured once, instead of the repeated re-measuring nested auto-size TableLayoutPanels do.
    /// Children stretch to the available width when they are containers or have Dock = Fill/Top; otherwise they keep
    /// their own (or preferred) width. Heights come from the preferred size (AutoSize) or the control's Height.
    /// </summary>
    internal abstract class ArrangePanel : Panel
    {
        [ThreadStatic] static int arranging;

        protected ArrangePanel()
        {
            SuspendLayout();
            AutoSize = true;
            AutoSizeMode = AutoSizeMode.GrowAndShrink;
            Margin = Padding.Empty;
        }

        /// <summary>Lays children out for the given width and returns the height needed; bounds are set only when apply is true.</summary>
        protected abstract int ArrangeCore(int width, bool apply);

        internal int ArrangeChild(int width, bool apply) { return ArrangeCore(width, apply); }

        public override Size GetPreferredSize(Size proposedSize)
        {
            int w = proposedSize.Width > 1 && proposedSize.Width < 100000 ? proposedSize.Width : Width;
            arranging++;
            try { return new Size(w, ArrangeCore(w, false)); }
            finally { arranging--; }
        }

        protected override void OnLayout(LayoutEventArgs e)
        {
            if (arranging > 0) return;
            Control top = this;
            while (top.Parent is ArrangePanel) top = top.Parent;
            if (top != this)
            {
                // Something inside changed: let the host resize the top container if its height changed, then
                // re-arrange from the top (the host only re-lays it out when its size actually changes).
                if (top.Parent != null) top.Parent.PerformLayout();
                top.PerformLayout();
                return;
            }
            arranging++;
            try { ArrangeCore(ClientSize.Width, true); }
            finally { arranging--; }
        }

        /// <summary>Forces a full arrange of every top-level ArrangePanel under c.</summary>
        public static void ArrangeTops(Control c)
        {
            foreach (Control ch in c.Controls)
            {
                if (ch is ArrangePanel) ch.PerformLayout();
                else ArrangeTops(ch);
            }
        }

        protected static bool Shown(Control c)
        {
            var b = c as Banner;
            return b == null || b.IsShown;
        }

        /// <summary>Measures (and when apply is set, places) one child inside a slot of the given width; returns its height plus margins.</summary>
        protected static int Place(Control c, int x, int y, int width, bool apply)
        {
            if (!Shown(c)) return 0;
            var m = c.Margin;
            int avail = Math.Max(0, width - m.Horizontal);
            var wl = c as WrapLabel;
            if (wl != null) wl.FitTo(avail);
            int w, h;
            var ap = c as ArrangePanel;
            if (ap != null)
            {
                w = avail;
                h = ap.ArrangeChild(w, apply);
            }
            else
            {
                bool stretch = c.Dock == DockStyle.Fill || c.Dock == DockStyle.Top;
                if (c.AutoSize)
                {
                    var p = c.GetPreferredSize(new Size(avail, 0));
                    w = stretch ? avail : Math.Min(avail, p.Width);
                    h = p.Height;
                }
                else
                {
                    // Fixed-size controls keep their own width (never clamp it: the width is also their desired size).
                    w = stretch ? avail : c.Width;
                    h = c.Height;
                }
            }
            if (apply) c.SetBounds(x + m.Left, y + m.Top, w, h);
            return h + m.Vertical;
        }
    }

    /// <summary>Children stacked top to bottom.</summary>
    internal class VStack : ArrangePanel
    {
        public T Add<T>(T c) where T : Control
        {
            Controls.Add(c);
            return c;
        }

        protected override int ArrangeCore(int width, bool apply)
        {
            int y = Padding.Top, inner = width - Padding.Horizontal;
            foreach (Control c in Controls) y += Place(c, Padding.Left, y, inner, apply);
            return y + Padding.Bottom;
        }
    }

    /// <summary>White container with a title; children are stacked as rows.</summary>
    internal class Card : VStack
    {
        readonly Label title;

        public Card(string titleText)
        {
            DoubleBuffered = true;
            SetStyle(ControlStyles.ResizeRedraw, true);
            BackColor = Theme.CardBg;
            Padding = new Padding(Theme.S(16), Theme.S(12), Theme.S(16), Theme.S(14));
            Margin = new Padding(Theme.S(7));
            if (titleText != null)
            {
                title = new Label();
                title.Text = titleText;
                title.Font = Theme.CardTitle;
                title.ForeColor = Theme.Text;
                title.AutoSize = true;
                title.UseMnemonic = false;
                title.Margin = new Padding(0, 0, 0, Theme.S(8));
                Add(title);
            }
        }

        public string Title { get { return title == null ? "" : title.Text; } set { if (title != null) title.Text = value; } }

        public T AddRow<T>(T c) where T : Control { return Add(c); }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            using (var p = new Pen(Theme.Border)) e.Graphics.DrawRectangle(p, 0, 0, Width - 1, Height - 1);
        }
    }

    /// <summary>Two-column label/control grid; labels share one column width, controls keep their own widths.</summary>
    internal class FieldGrid : ArrangePanel
    {
        readonly System.Collections.Generic.List<Control[]> rows = new System.Collections.Generic.List<Control[]>();

        public FieldGrid()
        {
            BackColor = Theme.CardBg;
        }

        public T Add<T>(string label, T control) where T : Control
        {
            var l = Theme.Lbl(label);
            Controls.Add(l);
            Controls.Add(control);
            rows.Add(new Control[] { l, control });
            return control;
        }

        public T AddFull<T>(T control) where T : Control
        {
            Controls.Add(control);
            rows.Add(new Control[] { null, control });
            return control;
        }

        static Size Pref(Control c, int avail)
        {
            if (c is ArrangePanel || c.AutoSize) return c.GetPreferredSize(new Size(avail, 0));
            return c.Size;
        }

        protected override int ArrangeCore(int width, bool apply)
        {
            int inner = width - Padding.Horizontal;
            int lw = 0;
            foreach (var r in rows)
                if (r[0] != null && Shown(r[1])) lw = Math.Max(lw, r[0].GetPreferredSize(Size.Empty).Width + r[0].Margin.Horizontal);
            int y = Padding.Top, x0 = Padding.Left;
            foreach (var r in rows)
            {
                Control lab = r[0], ctl = r[1];
                if (!Shown(ctl)) continue;
                if (lab == null)
                {
                    y += Place(ctl, x0, y, inner, apply);
                    continue;
                }
                var lm = lab.Margin;
                var cm = ctl.Margin;
                int cavail = Math.Max(0, inner - lw - cm.Horizontal);
                Size lp = lab.GetPreferredSize(Size.Empty);
                Size cp = Pref(ctl, cavail);
                int cwid = ctl.Dock == DockStyle.Fill ? cavail : cp.Width;
                int h = Math.Max(lp.Height + lm.Vertical, cp.Height + cm.Vertical);
                if (apply)
                {
                    lab.SetBounds(x0 + lm.Left, y + (h - lp.Height) / 2, Math.Max(0, lw - lm.Horizontal), lp.Height);
                    var ap = ctl as ArrangePanel;
                    if (ap != null) ap.ArrangeChild(cwid, true);
                    ctl.SetBounds(x0 + lw + cm.Left, y + (h - cp.Height) / 2, cwid, cp.Height);
                }
                y += h;
            }
            return y + Padding.Bottom;
        }
    }

    /// <summary>Root of a page: optional full-width rows on top, then columns sized by percentages.</summary>
    internal class ColumnsPanel : ArrangePanel
    {
        readonly float[] pct;
        readonly VStack[] cols;
        readonly System.Collections.Generic.List<Control> top = new System.Collections.Generic.List<Control>();

        public ColumnsPanel(float[] percents)
        {
            pct = percents;
            cols = new VStack[percents.Length];
            for (int i = 0; i < cols.Length; i++)
            {
                cols[i] = new VStack();
                cols[i].BackColor = Theme.Bg;
                Controls.Add(cols[i]);
            }
        }

        public VStack Col(int i) { return cols[i]; }

        public T AddTop<T>(T c) where T : Control
        {
            Controls.Add(c);
            top.Add(c);
            return c;
        }

        protected override int ArrangeCore(int width, bool apply)
        {
            int inner = width - Padding.Horizontal;
            int y = Padding.Top;
            foreach (var c in top) y += Place(c, Padding.Left, y, inner, apply);
            float sum = 0;
            foreach (float p in pct) sum += p;
            int x = Padding.Left, maxH = 0;
            for (int i = 0; i < cols.Length; i++)
            {
                int w = i == cols.Length - 1 ? Padding.Left + inner - x : (int)Math.Round(inner * pct[i] / sum);
                maxH = Math.Max(maxH, Place(cols[i], x, y, w, apply));
                x += w;
            }
            return y + maxH + Padding.Bottom;
        }
    }

    /// <summary>Fixed-height tiles in a grid of equal-width columns.</summary>
    internal class TileGrid : ArrangePanel
    {
        readonly int columns;

        public TileGrid(int columnCount)
        {
            columns = Math.Max(1, columnCount);
        }

        public T Add<T>(T c) where T : Control
        {
            Controls.Add(c);
            return c;
        }

        protected override int ArrangeCore(int width, bool apply)
        {
            int n = Controls.Count, inner = width - Padding.Horizontal;
            int cw = inner / columns, y = Padding.Top;
            for (int i = 0; i < n; i += columns)
            {
                int rowH = 0;
                for (int k = 0; k < columns && i + k < n; k++)
                {
                    var c = Controls[i + k];
                    var m = c.Margin;
                    if (apply) c.SetBounds(Padding.Left + k * cw + m.Left, y + m.Top, Math.Max(0, cw - m.Horizontal), c.Height);
                    rowH = Math.Max(rowH, c.Height + m.Vertical);
                }
                y += rowH;
            }
            return y + Padding.Bottom;
        }
    }

    /// <summary>Horizontal strip of controls.</summary>
    internal class Strip : FlowLayoutPanel
    {
        public Strip() : this(false) { }

        public Strip(bool wrap)
        {
            SuspendLayout();
            FlowDirection = FlowDirection.LeftToRight;
            WrapContents = wrap;
            AutoSize = true;
            AutoSizeMode = AutoSizeMode.GrowAndShrink;
            Dock = DockStyle.Fill;
            Margin = Padding.Empty;
            BackColor = Color.Transparent;
        }

        public T Add<T>(T c) where T : Control
        {
            if (c.Margin == Padding.Empty || c.Margin.Right == 0) c.Margin = new Padding(0, Theme.S(3), Theme.S(8), Theme.S(3));
            Controls.Add(c);
            return c;
        }
    }

    internal class NavButton : Control
    {
        public string Glyph;
        bool active, hover;

        public NavButton(string glyph, string text)
        {
            Glyph = glyph;
            Text = text;
            SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            Height = Theme.S(42);
            Dock = DockStyle.Top;
            Cursor = Cursors.Hand;
            BackColor = Theme.NavBg;
            Font = Theme.Nav;
        }

        public bool Active { get { return active; } set { active = value; Invalidate(); } }

        protected override void OnMouseEnter(EventArgs e) { hover = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { hover = false; Invalidate(); base.OnMouseLeave(e); }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.Clear(active || hover ? Theme.NavHover : Theme.NavBg);
            if (active)
                using (var b = new SolidBrush(Theme.NavActive)) g.FillRectangle(b, 0, 0, Theme.S(4), Height);
            Color fg = active ? Color.White : Theme.NavText;
            var iconRect = new Rectangle(Theme.S(14), 0, Theme.S(28), Height);
            TextRenderer.DrawText(g, Glyph, Theme.NavIcon, iconRect, active ? Theme.NavActive : fg,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix);
            var textRect = new Rectangle(Theme.S(50), 0, Width - Theme.S(54), Height);
            TextRenderer.DrawText(g, Text, Font, textRect, fg, TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
        }
    }

    /// <summary>Metric tile: caption, big value and a sub line.</summary>
    internal class Tile : Control
    {
        string caption, value = "—", sub = "";
        Color valueColor = Theme.Text;

        public Tile(string captionText)
        {
            caption = captionText;
            SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            Height = Theme.S(82);
            Dock = DockStyle.Fill;
            Margin = new Padding(Theme.S(5));
            BackColor = Theme.CardBg;
        }

        public void Set(string v, string s, Color c)
        {
            value = v; sub = s ?? ""; valueColor = c;
            Invalidate();
        }

        public void Set(string v, string s) { Set(v, s, Theme.Text); }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.Clear(Theme.GridAlt);
            using (var p = new Pen(Theme.Border)) g.DrawRectangle(p, 0, 0, Width - 1, Height - 1);
            int x = Theme.S(12);
            const TextFormatFlags tf = TextFormatFlags.Left | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix;
            TextRenderer.DrawText(g, caption.ToUpperInvariant(), Theme.SmallBold, new Rectangle(x, Theme.S(8), Width - x, Theme.S(18)), Theme.Muted, tf);
            TextRenderer.DrawText(g, value, Theme.Big, new Rectangle(x - Theme.S(2), Theme.S(25), Width - x, Theme.S(32)), valueColor, tf);
            TextRenderer.DrawText(g, sub, Theme.Small, new Rectangle(x, Theme.S(57), Width - x - Theme.S(4), Theme.S(20)), Theme.Muted, tf);
        }
    }

    /// <summary>Auto-sized label that wraps at its design width, or narrower when the layout has less room.</summary>
    internal class WrapLabel : Label
    {
        int designWidth;

        public WrapLabel(int maxWidth)
        {
            AutoSize = true;
            UseMnemonic = false;
            designWidth = maxWidth;
            MaximumSize = new Size(maxWidth, 0);
        }

        public void FitTo(int available)
        {
            int w = Math.Max(Theme.S(80), Math.Min(designWidth, available));
            if (MaximumSize.Width != w) MaximumSize = new Size(w, 0);
        }
    }

    /// <summary>Coloured notice line.</summary>
    internal class Banner : WrapLabel
    {
        /// <summary>The banner's own visibility (Visible also turns false while its page is hidden).</summary>
        public bool IsShown { get; private set; }

        protected override void SetVisibleCore(bool value)
        {
            IsShown = value;
            base.SetVisibleCore(value);
        }

        public Banner() : base(Theme.S(1100))
        {
            Font = Theme.Body;
            Padding = new Padding(Theme.S(10), Theme.S(6), Theme.S(10), Theme.S(6));
            Margin = new Padding(Theme.S(7), Theme.S(4), Theme.S(7), Theme.S(4));
            Visible = false;
        }

        public void Show(string text, bool warning)
        {
            Text = text;
            BackColor = warning ? Theme.WarnBg : Theme.InfoBg;
            ForeColor = warning ? Theme.Warn : Theme.Info;
            Visible = true;
        }

        public void ShowGood(string text)
        {
            Text = text;
            BackColor = Theme.AccentSoft;
            ForeColor = Theme.AccentDark;
            Visible = true;
        }
    }

    /// <summary>Scrollable page with a column layout of cards.</summary>
    internal class PageBase : Panel
    {
        protected readonly AppState State;
        public readonly string Title;
        public readonly string Subtitle;
        protected readonly ColumnsPanel Root;

        public PageBase(AppState state, string title, string subtitle, params float[] columnPercents)
        {
            State = state;
            Title = title;
            Subtitle = subtitle;
            SuspendLayout();
            Dock = DockStyle.Fill;
            AutoScroll = true;
            BackColor = Theme.Bg;
            Padding = new Padding(Theme.S(10), Theme.S(6), Theme.S(10), Theme.S(10));
            if (columnPercents == null || columnPercents.Length == 0) columnPercents = new float[] { 100 };
            Root = new ColumnsPanel(columnPercents);
            Root.Dock = DockStyle.Top;
            Root.BackColor = Theme.Bg;
            Controls.Add(Root);
        }

        /// <summary>The vertical stack of cards in one column of the page.</summary>
        protected VStack Column(int col)
        {
            return Root.Col(col);
        }

        protected static T Stack<T>(VStack column, T c) where T : Control
        {
            return column.Add(c);
        }

        /// <summary>Full-width row above the columns.</summary>
        protected T FullRow<T>(T c) where T : Control
        {
            Root.AddTop(c);
            return c;
        }

        public virtual void OnActivated() { }

        /// <summary>Runs a refresh that touches many controls with a single layout pass at the end.</summary>
        protected void Batch(Action action)
        {
            UiLayout.Batch(this, action);
        }
    }

    internal static class GridUtil
    {
        public static void FitHeight(DataGridView g, int maxRows)
        {
            int rows = Math.Min(Math.Max(g.Rows.Count, 1), maxRows);
            g.Height = g.ColumnHeadersHeight + rows * g.RowTemplate.Height + 2;
            if (g.ReadOnly) g.ClearSelection();
        }

        /// <summary>Read-only display grids show no selection highlight.</summary>
        public static void NoSelection(DataGridView g)
        {
            g.SelectionChanged += (s, e) => g.ClearSelection();
            g.DefaultCellStyle.SelectionBackColor = g.DefaultCellStyle.BackColor;
            g.DefaultCellStyle.SelectionForeColor = g.DefaultCellStyle.ForeColor;
            g.AlternatingRowsDefaultCellStyle.SelectionBackColor = g.AlternatingRowsDefaultCellStyle.BackColor;
            g.TabStop = false;
        }
    }
}
