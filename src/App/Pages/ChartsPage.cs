using System;
using System.Drawing;
using System.Globalization;
using System.Windows.Forms;
using System.Windows.Forms.DataVisualization.Charting;
using CalcBJ.Engine;

namespace CalcBJ.App
{
    internal sealed class ChartsPage : PageBase
    {
        readonly Chart freq, bets, contrib, dist;
        readonly Banner banner;
        readonly NumericUpDown hours;

        public ChartsPage(AppState s)
            : base(s, "Charts", "How often each count comes up, what it is worth, and how your bets line up with it.", 50, 50)
        {
            var left = Column(0);
            var right = Column(1);
            banner = FullRow(new Banner());

            var c1 = Stack(left, new Card("Count frequency and player advantage"));
            freq = c1.AddRow(ChartUtil.Create("Count", "Share of rounds (%)", 300));
            var c2 = Stack(right, new Card("Your bet ramp against the advantage"));
            bets = c2.AddRow(ChartUtil.Create("Count", "Bet (units × hands)", 300));
            var c3 = Stack(left, new Card("Contribution to win rate by count"));
            contrib = c3.AddRow(ChartUtil.Create("Count", "Win per 100 rounds ($)", 300));
            var c4 = Stack(right, new Card("Distribution of results"));
            var g = c4.AddRow(new FieldGrid());
            hours = g.Add("After this many hours", Theme.Num(1, 100000, 100, 0, 10, 100));
            dist = c4.AddRow(ChartUtil.Create("Result ($)", "Probability density", 262));

            hours.ValueChanged += (o, e) => Refresh2();
            State.BettingChanged += (o, e) => { if (Visible) Refresh2(); };
            State.SimChanged += (o, e) => { if (Visible && !State.SimRunning) Refresh2(); };
            State.Reloaded += (o, e) => { if (Visible) Refresh2(); };
        }

        public override void OnActivated() { Refresh2(); }

        void Refresh2() { Batch(Refresh2Core); }


        void Refresh2Core()
        {
            foreach (var c in new[] { freq, bets, contrib, dist }) { ChartUtil.Clear(c); ChartUtil.Message(c, ""); }
            var a = State.Result;
            if (a == null || !a.Valid || a.RowFreq == null)
            {
                banner.Show(State.Sim == null ? "No simulation yet — press Run simulation (F5)." : "The current ramp never bets.", false);
                return;
            }
            if (State.SimStale) banner.Show("⚠  Settings changed since the last simulation; charts show the previous settings.", true);
            else banner.Visible = false;

            var r = State.Ramp;
            var sF = ChartUtil.Add(freq, "Frequency (%)", SeriesChartType.Column, Color.FromArgb(150, ChartUtil.Series2));
            var sE = ChartUtil.Add(freq, "Advantage per hand (%)", SeriesChartType.Line, ChartUtil.Series3);
            sE.YAxisType = AxisType.Secondary;
            sE.MarkerStyle = MarkerStyle.Circle; sE.MarkerSize = Theme.S(5);
            var sB = ChartUtil.Add(bets, "Bet (units)", SeriesChartType.Column, Color.FromArgb(170, ChartUtil.Series1));
            var sE2 = ChartUtil.Add(bets, "Advantage per hand (%)", SeriesChartType.Line, ChartUtil.Series3);
            sE2.YAxisType = AxisType.Secondary;
            sE2.MarkerStyle = MarkerStyle.Circle; sE2.MarkerSize = Theme.S(5);
            var sC = ChartUtil.Add(contrib, "Win per 100 rounds", SeriesChartType.Column, ChartUtil.Series1);

            for (int i = 0; i < r.Rows; i++)
            {
                string label = Theme.CountLabel(r.Min + i, r.Min, r.Max);
                double x = i + 1;
                int p = sF.Points.AddXY(x, a.RowFreq[i] * 100);
                sF.Points[p].AxisLabel = label;
                if (a.RowN[i] > 0) { p = sE.Points.AddXY(x, a.RowEV[i] * 100); sE.Points[p].AxisLabel = label; }
                p = sB.Points.AddXY(x, r.Units[i] * Math.Max(1, r.Hands[i]));
                sB.Points[p].AxisLabel = label;
                if (r.Units[i] <= 0) sB.Points[p].Color = Theme.Border;
                if (a.RowN[i] > 0) { p = sE2.Points.AddXY(x, a.RowEV[i] * 100); sE2.Points[p].AxisLabel = label; }
                double w = a.RowContribution[i] * 100 * State.Unit;
                p = sC.Points.AddXY(x, w);
                sC.Points[p].AxisLabel = label;
                sC.Points[p].Color = w >= 0 ? ChartUtil.Series1 : Theme.Bad;
            }
            foreach (var c in new[] { freq, bets, contrib })
            {
                var ax = c.ChartAreas[0];
                ax.AxisX.Interval = 1;
                ax.AxisX.LabelStyle.Angle = r.Rows > 16 ? -90 : 0;
                ax.AxisX.MajorGrid.Enabled = false;
            }
            freq.ChartAreas[0].AxisY2.Title = "Advantage (%)";
            bets.ChartAreas[0].AxisY2.Title = "Advantage (%)";
            contrib.ChartAreas[0].AxisY.LabelStyle.Format = "$#,0.##";

            double hrs = (double)hours.Value;
            double n = hrs * a.RoundsPerHour;
            double mu = a.EV * State.Unit * n, sd = Math.Sqrt(a.Var * n) * State.Unit;
            if (sd > 0)
            {
                var sDist = ChartUtil.Add(dist, "Result after " + hrs.ToString("#,0", CultureInfo.InvariantCulture) + " h", SeriesChartType.Area, Color.FromArgb(110, ChartUtil.Series2));
                var sLoss = ChartUtil.Add(dist, "Losing outcomes", SeriesChartType.Area, Color.FromArgb(120, Theme.Bad));
                double lo = mu - 3.5 * sd, hi = mu + 3.5 * sd;
                for (int k = 0; k <= 200; k++)
                {
                    double x = lo + (hi - lo) * k / 200.0;
                    double z = (x - mu) / sd;
                    double y = Math.Exp(-0.5 * z * z) / (sd * Math.Sqrt(2 * Math.PI));
                    sDist.Points.AddXY(x, y);
                    sLoss.Points.AddXY(x, x < 0 ? y : 0);
                }
                var ax = dist.ChartAreas[0];
                ChartUtil.NiceX(dist, lo, hi, 6);
                ax.AxisX.LabelStyle.Format = "$#,0";
                ax.AxisY.LabelStyle.Enabled = false;
                ax.AxisY.MajorGrid.Enabled = false;
                ChartUtil.Message(dist, string.Format(CultureInfo.InvariantCulture, "Expected {0}, SD {1}, chance of losing {2}",
                    Theme.Money0(mu), Theme.Money0(sd), Theme.Pct(Analysis.Phi(-mu / sd), 1)));
            }
        }
    }
}
