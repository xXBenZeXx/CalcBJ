using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.Text;
using System.Windows.Forms;
using CalcBJ.Engine;

namespace CalcBJ.App
{
    /// <summary>Colour-coded strategy chart (hard totals, soft totals or pairs) with index annotations.</summary>
    internal sealed class StrategyChart : DataGridView
    {
        public enum ChartKind { Hard, Soft, Pair }

        readonly ChartKind kind;
        readonly int[] totals;
        string[,] codes;
        string[,] marks;
        string[,] tips;
        static readonly Font MarkFont = new Font("Segoe UI", 6.75f);
        static readonly Font CodeFont = new Font("Segoe UI Semibold", 9f);

        public StrategyChart(ChartKind k)
        {
            kind = k;
            Theme.StyleGrid(this);
            ReadOnly = true;
            ScrollBars = ScrollBars.None;
            CellBorderStyle = DataGridViewCellBorderStyle.Single;
            DefaultCellStyle.SelectionBackColor = DefaultCellStyle.BackColor;
            AlternatingRowsDefaultCellStyle.BackColor = Theme.CardBg;
            RowTemplate.Height = Theme.S(27);
            ShowCellToolTips = true;
            if (k == ChartKind.Hard) totals = new[] { 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16, 17, 18, 19, 20 };
            else if (k == ChartKind.Soft) totals = new[] { 13, 14, 15, 16, 17, 18, 19, 20 };
            else totals = new[] { 2, 3, 4, 5, 6, 7, 8, 9, 10, 1 };

            var lc = Theme.TextCol(k == ChartKind.Hard ? "Hard" : (k == ChartKind.Soft ? "Soft" : "Pair"), 52, false, true);
            lc.DefaultCellStyle.Font = Theme.BodyBold;
            lc.DefaultCellStyle.BackColor = Theme.GridHeader;
            lc.DefaultCellStyle.SelectionBackColor = Theme.GridHeader;
            Columns.Add(lc);
            foreach (int u in Cards.ChartUpOrder)
            {
                var c = Theme.TextCol(Cards.RankName(u), 44, false, true);
                c.DefaultCellStyle.Alignment = DataGridViewContentAlignment.MiddleCenter;
                c.HeaderCell.Style.Alignment = DataGridViewContentAlignment.MiddleCenter;
                Columns.Add(c);
            }
            foreach (int t in totals) Rows.Add(RowLabel(t));
            Height = ColumnHeadersHeight + totals.Length * RowTemplate.Height + 2;
            Width = Theme.S(52) + 10 * Theme.S(44) + 2;
            Dock = DockStyle.None;
            Anchor = AnchorStyles.Left | AnchorStyles.Top;
            CellPainting += OnCellPainting;
            CellToolTipTextNeeded += (s, e) =>
            {
                if (tips != null && e.RowIndex >= 0 && e.ColumnIndex >= 1) e.ToolTipText = tips[e.RowIndex, e.ColumnIndex - 1];
            };
            SelectionChanged += (s, e) => ClearSelection();
        }

        string RowLabel(int t)
        {
            if (kind == ChartKind.Hard) return t.ToString(CultureInfo.InvariantCulture);
            if (kind == ChartKind.Soft) return "A," + (t - 11).ToString(CultureInfo.InvariantCulture);
            string r = Cards.RankName(t);
            return r + "," + r;
        }

        static string ShortPlay(string play)
        {
            switch (play)
            {
                case "Stand": return "S";
                case "Hit": return "H";
                case "Double": return "D";
                case "No double": return "nD";
                case "Split": return "P";
                case "No split": return "nP";
                case "Surrender": return "R";
                case "No surrender": return "nR";
                default: return "I";
            }
        }

        public static string ShortIndex(IndexInfo i)
        {
            string p = ShortPlay(i.Play);
            if (i.Condition == "always") return p + "*";
            int v = (int)Math.Round(i.Threshold);
            string n = v > 0 ? "+" + v : v.ToString(CultureInfo.InvariantCulture);
            return p + (i.AtOrAbove ? "≥" : "≤") + n;
        }

        static string EvText(double[] ev, int cell)
        {
            if (ev == null) return "";
            string[] names = { "stand", "hit", "double", "surrender", "split" };
            var sb = new StringBuilder();
            for (int k = 0; k < 5; k++)
            {
                double v = ev[cell * 5 + k];
                if (double.IsNaN(v)) continue;
                if (sb.Length > 0) sb.Append("   ");
                sb.Append(names[k]).Append(' ').Append(v.ToString("+0.000;-0.000", CultureInfo.InvariantCulture));
            }
            return sb.ToString();
        }

        /// <summary>Shows the strategy at a count value, or basic strategy when count is null.</summary>
        public void Render(PlayTable pt, int? count, bool showIndices, List<IndexInfo> indices, string countLabel)
        {
            int rows = totals.Length;
            codes = new string[rows, 10];
            marks = new string[rows, 10];
            tips = new string[rows, 10];
            var lookup = new Dictionary<int, List<IndexInfo>>();
            if (indices != null)
                foreach (var i in indices)
                {
                    HandKind want = kind == ChartKind.Hard ? HandKind.Hard : (kind == ChartKind.Soft ? HandKind.Soft : HandKind.Pair);
                    if (i.Kind != want) continue;
                    int key = i.Total * 16 + i.Upcard;
                    List<IndexInfo> l;
                    if (!lookup.TryGetValue(key, out l)) { l = new List<IndexInfo>(); lookup[key] = l; }
                    l.Add(i);
                }
            CAResult ca = count.HasValue ? pt.ResultAt(count.Value) : pt.BasicResult;
            for (int r = 0; r < rows; r++)
            {
                int t = totals[r];
                for (int c = 0; c < 10; c++)
                {
                    int u = Cards.ChartUpOrder[c];
                    int cell;
                    byte f;
                    string code;
                    if (kind == ChartKind.Pair)
                    {
                        cell = PlayTable.PairCell(t, u);
                        f = count.HasValue ? pt.FlagsAt(count.Value, cell) : pt.Basic[cell];
                        code = StrategyBuilder.ActionCode(f, true);
                        if (code == "")
                        {
                            int tc = t == 1 ? PlayTable.SoftCell(12, u) : PlayTable.HardCell(2 * t, u);
                            byte tf = count.HasValue ? pt.FlagsAt(count.Value, tc) : pt.Basic[tc];
                            code = StrategyBuilder.ActionCode(tf, false);
                        }
                    }
                    else
                    {
                        cell = kind == ChartKind.Hard ? PlayTable.HardCell(t, u) : PlayTable.SoftCell(t, u);
                        f = count.HasValue ? pt.FlagsAt(count.Value, cell) : pt.Basic[cell];
                        code = StrategyBuilder.ActionCode(f, false);
                    }
                    codes[r, c] = code;
                    List<IndexInfo> il;
                    string mark = "";
                    var tip = new StringBuilder();
                    tip.Append(RowLabel(t)).Append(" vs ").Append(Cards.RankName(u)).Append(" — ").Append(Describe(code));
                    tip.Append(count.HasValue ? " at " + countLabel : " (basic strategy)");
                    if (lookup.TryGetValue(t * 16 + u, out il))
                    {
                        foreach (var i in il)
                        {
                            if (mark.Length > 0) mark += " ";
                            mark += ShortIndex(i);
                            tip.Append("\nIndex: ").Append(i.Text);
                        }
                    }
                    string ev = EvText(ca == null ? null : ca.CellEV, cell);
                    if (ev.Length > 0) tip.Append("\nEV per unit: ").Append(ev);
                    marks[r, c] = showIndices ? mark : "";
                    tips[r, c] = tip.ToString();
                }
            }
            Invalidate();
        }

        static string Describe(string code)
        {
            switch (code)
            {
                case "H": return "Hit";
                case "S": return "Stand";
                case "D": return "Double, otherwise hit";
                case "Ds": return "Double, otherwise stand";
                case "P": return "Split";
                case "R": return "Surrender, otherwise hit";
                case "Rs": return "Surrender, otherwise stand";
                case "Rp": return "Surrender, otherwise split";
                default: return code;
            }
        }

        void OnCellPainting(object sender, DataGridViewCellPaintingEventArgs e)
        {
            if (e.RowIndex < 0 || e.ColumnIndex < 1 || codes == null) return;
            int r = e.RowIndex, c = e.ColumnIndex - 1;
            string code = codes[r, c];
            var g = e.Graphics;
            var rect = e.CellBounds;
            using (var b = new SolidBrush(Theme.ActionColor(code))) g.FillRectangle(b, rect);
            using (var p = new Pen(Color.White)) g.DrawRectangle(p, rect.X, rect.Y, rect.Width - 1, rect.Height - 1);
            string mark = marks[r, c];
            bool hasMark = !string.IsNullOrEmpty(mark);
            var codeRect = hasMark ? new Rectangle(rect.X, rect.Y + 1, rect.Width, rect.Height - Theme.S(10)) : rect;
            TextRenderer.DrawText(g, code, CodeFont, codeRect, Theme.Text, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
            if (hasMark)
            {
                var mr = new Rectangle(rect.X, rect.Bottom - Theme.S(12), rect.Width, Theme.S(11));
                TextRenderer.DrawText(g, mark, MarkFont, mr, Theme.IndexMark, TextFormatFlags.HorizontalCenter | TextFormatFlags.Top | TextFormatFlags.NoPadding | TextFormatFlags.EndEllipsis);
                using (var b = new SolidBrush(Theme.IndexMark))
                    g.FillPolygon(b, new[] { new Point(rect.Right - Theme.S(7), rect.Y + 1), new Point(rect.Right - 1, rect.Y + 1), new Point(rect.Right - 1, rect.Y + Theme.S(7)) });
            }
            e.Handled = true;
        }
    }
}
