using System;
using System.Drawing;
using System.Globalization;
using System.Windows.Forms;

namespace CalcBJ.App
{
    internal static class Theme
    {
        public static float Scale = 1f;
        public static int S(double px) { return (int)Math.Round(px * Scale); }

        public static readonly Color NavBg = Hex("#13202A");
        public static readonly Color NavHover = Hex("#1D2F3C");
        public static readonly Color NavText = Hex("#B4C2CE");
        public static readonly Color NavActive = Hex("#25A36F");
        public static readonly Color Accent = Hex("#1F8F5F");
        public static readonly Color AccentDark = Hex("#17734C");
        public static readonly Color AccentSoft = Hex("#E2F3EA");
        public static readonly Color Bg = Hex("#F1F3F6");
        public static readonly Color CardBg = Color.White;
        public static readonly Color Border = Hex("#DCE2E8");
        public static readonly Color Text = Hex("#1C2833");
        public static readonly Color Muted = Hex("#687886");
        public static readonly Color Good = Hex("#1F8F5F");
        public static readonly Color Bad = Hex("#C0392B");
        public static readonly Color Warn = Hex("#9A6412");
        public static readonly Color WarnBg = Hex("#FFF3D6");
        public static readonly Color Info = Hex("#2C66A8");
        public static readonly Color InfoBg = Hex("#E7F0FA");
        public static readonly Color GridHeader = Hex("#F0F3F6");
        public static readonly Color GridAlt = Hex("#FAFBFC");
        public static readonly Color GridSel = Hex("#D8EEE2");

        public static readonly Color CellHit = Hex("#E9EEF2");
        public static readonly Color CellStand = Hex("#FFE7A3");
        public static readonly Color CellDouble = Hex("#B5E4BF");
        public static readonly Color CellSplit = Hex("#BCD7FF");
        public static readonly Color CellSurrender = Hex("#F5BDBD");
        public static readonly Color IndexMark = Hex("#E0701B");

        public static Font Body, BodyBold, Small, SmallBold, CardTitle, PageTitle, Big, Huge, Nav, NavIcon, Brand, Mono;

        public static void Init()
        {
            using (var g = Graphics.FromHwnd(IntPtr.Zero)) Scale = g.DpiX / 96f;
            const string ui = "Segoe UI";
            Body = new Font(ui, 9f);
            BodyBold = new Font("Segoe UI Semibold", 9f);
            Small = new Font(ui, 8f);
            SmallBold = new Font("Segoe UI Semibold", 8f);
            CardTitle = new Font("Segoe UI Semibold", 10.5f);
            PageTitle = new Font("Segoe UI Semibold", 15f);
            Big = new Font("Segoe UI Semibold", 15f);
            Huge = new Font("Segoe UI Semibold", 26f);
            Nav = new Font(ui, 10f);
            NavIcon = new Font("Segoe UI Symbol", 12f);
            Brand = new Font("Segoe UI Semibold", 16f);
            Mono = new Font("Consolas", 9f);
        }

        public static Color Hex(string s)
        {
            s = s.TrimStart('#');
            return Color.FromArgb(int.Parse(s.Substring(0, 2), NumberStyles.HexNumber),
                int.Parse(s.Substring(2, 2), NumberStyles.HexNumber), int.Parse(s.Substring(4, 2), NumberStyles.HexNumber));
        }

        public static Color ActionColor(string code)
        {
            if (string.IsNullOrEmpty(code)) return CardBg;
            switch (code[0])
            {
                case 'S': return CellStand;
                case 'D': return CellDouble;
                case 'P': return CellSplit;
                case 'R': return CellSurrender;
                default: return CellHit;
            }
        }

        public static void StyleGrid(DataGridView g)
        {
            g.BorderStyle = BorderStyle.None;
            g.BackgroundColor = CardBg;
            g.GridColor = Border;
            g.RowHeadersVisible = false;
            g.AllowUserToAddRows = false;
            g.AllowUserToDeleteRows = false;
            g.AllowUserToResizeRows = false;
            g.AllowUserToOrderColumns = false;
            g.MultiSelect = false;
            g.EnableHeadersVisualStyles = false;
            g.CellBorderStyle = DataGridViewCellBorderStyle.SingleHorizontal;
            g.ColumnHeadersBorderStyle = DataGridViewHeaderBorderStyle.None;
            g.ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.DisableResizing;
            g.ColumnHeadersHeight = S(30);
            g.RowTemplate.Height = S(25);
            g.ColumnHeadersDefaultCellStyle.BackColor = GridHeader;
            g.ColumnHeadersDefaultCellStyle.ForeColor = Muted;
            g.ColumnHeadersDefaultCellStyle.Font = SmallBold;
            g.ColumnHeadersDefaultCellStyle.SelectionBackColor = GridHeader;
            g.ColumnHeadersDefaultCellStyle.Padding = new Padding(S(4), 0, S(4), 0);
            g.DefaultCellStyle.Font = Body;
            g.DefaultCellStyle.ForeColor = Text;
            g.DefaultCellStyle.BackColor = CardBg;
            g.DefaultCellStyle.SelectionBackColor = GridSel;
            g.DefaultCellStyle.SelectionForeColor = Text;
            g.DefaultCellStyle.Padding = new Padding(S(4), 0, S(4), 0);
            g.AlternatingRowsDefaultCellStyle.BackColor = GridAlt;
            g.SelectionMode = DataGridViewSelectionMode.CellSelect;
            g.ScrollBars = ScrollBars.Vertical;
            typeof(DataGridView).InvokeMember("DoubleBuffered",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.SetProperty,
                null, g, new object[] { true });
        }

        public static DataGridViewTextBoxColumn TextCol(string header, int width, bool right, bool readOnly)
        {
            var c = new DataGridViewTextBoxColumn();
            c.HeaderText = header;
            c.Width = S(width);
            c.ReadOnly = readOnly;
            c.SortMode = DataGridViewColumnSortMode.NotSortable;
            if (right)
            {
                c.DefaultCellStyle.Alignment = DataGridViewContentAlignment.MiddleRight;
                c.HeaderCell.Style.Alignment = DataGridViewContentAlignment.MiddleRight;
            }
            return c;
        }

        public static Button Primary(string text)
        {
            var b = new Button();
            b.Text = text;
            b.UseMnemonic = false;
            b.FlatStyle = FlatStyle.Flat;
            b.FlatAppearance.BorderSize = 0;
            b.BackColor = Accent;
            b.ForeColor = Color.White;
            b.FlatAppearance.MouseOverBackColor = AccentDark;
            b.FlatAppearance.MouseDownBackColor = AccentDark;
            b.Font = BodyBold;
            b.AutoSize = true;
            b.AutoSizeMode = AutoSizeMode.GrowAndShrink;
            b.Padding = new Padding(S(10), S(3), S(10), S(3));
            b.Cursor = Cursors.Hand;
            b.UseVisualStyleBackColor = false;
            return b;
        }

        public static Button Secondary(string text)
        {
            var b = new Button();
            b.Text = text;
            b.UseMnemonic = false;
            b.FlatStyle = FlatStyle.Flat;
            b.FlatAppearance.BorderColor = Border;
            b.FlatAppearance.BorderSize = 1;
            b.BackColor = CardBg;
            b.ForeColor = Text;
            b.FlatAppearance.MouseOverBackColor = GridHeader;
            b.Font = Body;
            b.AutoSize = true;
            b.AutoSizeMode = AutoSizeMode.GrowAndShrink;
            b.Padding = new Padding(S(8), S(2), S(8), S(2));
            b.Cursor = Cursors.Hand;
            b.UseVisualStyleBackColor = false;
            return b;
        }

        public static Label Lbl(string text)
        {
            var l = new Label();
            l.Text = text;
            l.AutoSize = true;
            l.UseMnemonic = false;
            l.Font = Body;
            l.ForeColor = Text;
            l.Margin = new Padding(0, S(6), S(10), S(4));
            return l;
        }

        public static Label MutedLbl(string text)
        {
            var l = Lbl(text);
            l.ForeColor = Muted;
            l.Font = Small;
            return l;
        }

        public static Label Wrap(string text, int maxWidth)
        {
            var l = new WrapLabel(S(maxWidth));
            l.Text = text;
            l.ForeColor = Muted;
            l.Font = Small;
            l.Margin = new Padding(0, S(6), S(10), S(4));
            return l;
        }

        public static ComboBox Combo(int width, params string[] items)
        {
            var c = new ComboBox();
            c.DropDownStyle = ComboBoxStyle.DropDownList;
            c.FlatStyle = FlatStyle.Flat;
            c.Font = Body;
            c.Width = S(width);
            c.Items.AddRange(items);
            c.Margin = new Padding(0, S(3), 0, S(3));
            return c;
        }

        public static NumericUpDown Num(decimal min, decimal max, decimal value, int decimals, decimal inc, int width)
        {
            var n = new NumericUpDown();
            n.Minimum = min; n.Maximum = max; n.DecimalPlaces = decimals; n.Increment = inc;
            n.Value = Math.Max(min, Math.Min(max, value));
            n.Width = S(width);
            n.Font = Body;
            n.TextAlign = HorizontalAlignment.Right;
            n.BorderStyle = BorderStyle.FixedSingle;
            n.Margin = new Padding(0, S(3), 0, S(3));
            n.ThousandsSeparator = max >= 10000;
            return n;
        }

        public static CheckBox Check(string text)
        {
            var c = new CheckBox();
            c.Text = text;
            c.AutoSize = true;
            c.Font = Body;
            c.ForeColor = Text;
            c.Margin = new Padding(0, S(5), S(10), S(3));
            return c;
        }

        public static string Money(double v)
        {
            if (double.IsNaN(v) || double.IsInfinity(v)) return "—";
            string s = Math.Abs(v) >= 1000 ? Math.Abs(v).ToString("#,0", CultureInfo.InvariantCulture) : Math.Abs(v).ToString("0.00", CultureInfo.InvariantCulture);
            return (v < 0 ? "-$" : "$") + s;
        }

        public static string Money0(double v)
        {
            if (double.IsNaN(v) || double.IsInfinity(v)) return "—";
            return (v < 0 ? "-$" : "$") + Math.Abs(v).ToString("#,0", CultureInfo.InvariantCulture);
        }

        public static string Pct(double fraction, int decimals)
        {
            if (double.IsNaN(fraction) || double.IsInfinity(fraction)) return "—";
            return (fraction * 100).ToString("F" + decimals, CultureInfo.InvariantCulture) + "%";
        }

        public static string SignedPct(double fraction, int decimals)
        {
            if (double.IsNaN(fraction) || double.IsInfinity(fraction)) return "—";
            string s = (fraction * 100).ToString("F" + decimals, CultureInfo.InvariantCulture) + "%";
            return fraction > 0 ? "+" + s : s;
        }

        public static string F(double v, string fmt)
        {
            if (double.IsNaN(v)) return "—";
            if (double.IsInfinity(v)) return "∞";
            return v.ToString(fmt, CultureInfo.InvariantCulture);
        }

        public static string Count(long n)
        {
            if (n >= 1000000000) return (n / 1e9).ToString(n % 1000000000 == 0 ? "0" : "0.##", CultureInfo.InvariantCulture) + "B";
            if (n >= 1000000) return (n / 1e6).ToString(n % 1000000 == 0 ? "0" : "0.#", CultureInfo.InvariantCulture) + "M";
            if (n >= 1000) return (n / 1e3).ToString("0.#", CultureInfo.InvariantCulture) + "K";
            return n.ToString(CultureInfo.InvariantCulture);
        }

        public static string CountLabel(int v, int min, int max)
        {
            string s = v > 0 ? "+" + v.ToString(CultureInfo.InvariantCulture) : v.ToString(CultureInfo.InvariantCulture);
            if (v == min && min != max) return "≤ " + s;
            if (v == max && min != max) return "≥ " + s;
            return s;
        }

        static ToolTip tips;

        public static void Tip(Control c, string text)
        {
            if (tips == null)
            {
                tips = new ToolTip();
                tips.AutoPopDelay = 15000;
                tips.InitialDelay = 400;
                tips.ReshowDelay = 100;
            }
            tips.SetToolTip(c, text);
        }

        public static bool TryParse(string s, out double v)
        {
            v = 0;
            if (s == null) return false;
            s = s.Trim().Replace("$", "").Replace("%", "").Replace("+", "").Replace("≥", "").Replace("≤", "");
            if (s.IndexOf('.') < 0) s = s.Replace(',', '.');
            else s = s.Replace(",", "");
            return double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out v);
        }
    }
}
