using System;
using System.Drawing;
using System.Windows.Forms;
using System.Windows.Forms.DataVisualization.Charting;

namespace CalcBJ.App
{
    internal static class ChartUtil
    {
        public static readonly Color Series1 = Theme.Hex("#1F8F5F");
        public static readonly Color Series2 = Theme.Hex("#2C66A8");
        public static readonly Color Series3 = Theme.Hex("#E0701B");
        public static readonly Color Series4 = Theme.Hex("#8E5BB5");

        public static Chart Create(string xTitle, string yTitle, int height)
        {
            var c = new Chart();
            c.BackColor = Theme.CardBg;
            c.Height = Theme.S(height);
            c.Dock = DockStyle.Fill;
            c.Margin = new Padding(0, Theme.S(4), 0, 0);
            c.AntiAliasing = AntiAliasingStyles.All;
            c.TextAntiAliasingQuality = TextAntiAliasingQuality.High;
            var a = new ChartArea("main");
            a.BackColor = Theme.CardBg;
            StyleAxis(a.AxisX, xTitle);
            StyleAxis(a.AxisY, yTitle);
            StyleAxis(a.AxisY2, "");
            a.AxisY2.MajorGrid.Enabled = false;
            c.ChartAreas.Add(a);
            var l = new Legend("legend");
            l.Docking = Docking.Top;
            l.Alignment = StringAlignment.Near;
            l.Font = Theme.Small;
            l.ForeColor = Theme.Muted;
            l.BackColor = Theme.CardBg;
            c.Legends.Add(l);
            return c;
        }

        static void StyleAxis(Axis ax, string title)
        {
            ax.Title = title;
            ax.TitleFont = Theme.Small;
            ax.TitleForeColor = Theme.Muted;
            ax.LabelStyle.Font = Theme.Small;
            ax.LabelStyle.ForeColor = Theme.Muted;
            ax.LineColor = Theme.Border;
            ax.MajorGrid.LineColor = Theme.Hex("#ECEFF2");
            ax.MajorTickMark.LineColor = Theme.Border;
            ax.MinorGrid.Enabled = false;
        }

        public static Series Add(Chart c, string name, SeriesChartType type, Color color)
        {
            var s = new Series(name);
            s.ChartType = type;
            s.Color = color;
            s.ChartArea = "main";
            s.Legend = "legend";
            s.BorderWidth = type == SeriesChartType.Line || type == SeriesChartType.Spline ? Math.Max(2, Theme.S(2)) : 1;
            c.Series.Add(s);
            return s;
        }

        public static void Clear(Chart c)
        {
            c.Series.Clear();
            var a = c.ChartAreas[0];
            a.AxisX.Minimum = double.NaN; a.AxisX.Maximum = double.NaN;
            a.AxisY.Minimum = double.NaN; a.AxisY.Maximum = double.NaN;
            a.AxisY.IsLogarithmic = false;
            a.AxisX.CustomLabels.Clear();
            a.AxisX.Interval = 0;
            a.AxisX.IntervalOffset = 0;
        }

        /// <summary>A 1/2/5 × 10^n step giving roughly the requested number of intervals.</summary>
        public static double NiceStep(double range, int intervals)
        {
            if (range <= 0 || double.IsNaN(range) || double.IsInfinity(range)) return 1;
            double raw = range / intervals;
            double mag = Math.Pow(10, Math.Floor(Math.Log10(raw)));
            double n = raw / mag;
            double step = n < 1.5 ? 1 : (n < 3 ? 2 : (n < 7 ? 5 : 10));
            return step * mag;
        }

        public static void NiceX(Chart c, double lo, double hi, int intervals)
        {
            double step = NiceStep(hi - lo, intervals);
            var ax = c.ChartAreas[0].AxisX;
            ax.Minimum = Math.Floor(lo / step) * step;
            ax.Maximum = Math.Ceiling(hi / step) * step;
            ax.Interval = step;
        }

        public static void Message(Chart c, string text)
        {
            c.Titles.Clear();
            if (string.IsNullOrEmpty(text)) return;
            var t = new Title(text);
            t.Font = Theme.Body;
            t.ForeColor = Theme.Muted;
            t.Docking = Docking.Top;
            c.Titles.Add(t);
        }
    }
}
