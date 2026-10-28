using System;
using System.Drawing;
using System.Globalization;
using System.Windows.Forms;
using System.Windows.Forms.DataVisualization.Charting;
using CalcBJ.Engine;

namespace CalcBJ.App
{
    internal sealed class RiskPage : PageBase
    {
        readonly NumericUpDown bankroll, tripHours, tripBank;
        readonly Tile tRor, tTripExp, tTripLose, tTripRuin, tTripBest, tTripWorst;
        readonly DataGridView needed, pct;
        readonly Chart rorChart, pathChart;
        readonly Banner banner;
        bool loading;

        static readonly double[] Targets = { 0.001, 0.005, 0.01, 0.02, 0.05, 0.10, 0.135, 0.20, 0.30 };
        static readonly double[] Percentiles = { 0.01, 0.05, 0.10, 0.25, 0.50, 0.75, 0.90, 0.95, 0.99 };

        public RiskPage(AppState s)
            : base(s, "Bankroll & risk", "Risk of ruin, bankroll requirements and what a trip can look like, for the current bet ramp.", 42, 58)
        {
            var left = Column(0);
            var right = Column(1);

            var inp = Stack(left, new Card("Inputs"));
            var g = inp.AddRow(new FieldGrid());
            bankroll = g.Add("Total bankroll ($)", Theme.Num(1, 1000000000, 25000, 0, 1000, 120));
            tripHours = g.Add("Trip length (hours)", Theme.Num(1, 100000, 40, 0, 5, 120));
            tripBank = g.Add("Trip bankroll ($)", Theme.Num(1, 1000000000, 10000, 0, 500, 120));
            inp.AddRow(Theme.Wrap("Lifetime risk of ruin assumes you keep betting the same unit forever (no resizing). "
                + "Trip figures use a normal approximation for the result after the trip and the reflection principle for "
                + "the chance of busting the trip bankroll at any point during it.", 470));

            var nc = Stack(left, new Card("Bankroll needed"));
            needed = nc.AddRow(new DataGridView());
            Theme.StyleGrid(needed);
            needed.ReadOnly = true;
            needed.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
            needed.Columns.Add(Theme.TextCol("Risk of ruin", 100, true, true));
            needed.Columns.Add(Theme.TextCol("Bankroll", 120, true, true));
            needed.Columns.Add(Theme.TextCol("Units", 90, true, true));
            needed.Columns.Add(Theme.TextCol("Max bets", 90, true, true));
            needed.Columns[3].AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill;
            needed.Dock = DockStyle.Fill;
            GridUtil.NoSelection(needed);

            var pc = Stack(left, new Card("Trip result percentiles"));
            pct = pc.AddRow(new DataGridView());
            Theme.StyleGrid(pct);
            pct.ReadOnly = true;
            pct.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
            pct.Columns.Add(Theme.TextCol("Percentile", 100, true, true));
            pct.Columns.Add(Theme.TextCol("Trip result", 130, true, true));
            pct.Columns.Add(Theme.TextCol("Per hour", 100, true, true));
            pct.Columns[2].AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill;
            pct.Dock = DockStyle.Fill;
            GridUtil.NoSelection(pct);

            banner = Stack(right, new Banner());
            var tc = Stack(right, new Card("Risk summary"));
            var tiles = tc.AddRow(new TileGrid(3));
            tRor = tiles.Add(new Tile("Lifetime risk of ruin")); tTripExp = tiles.Add(new Tile("Expected trip result")); tTripLose = tiles.Add(new Tile("Chance of a losing trip"));
            tTripRuin = tiles.Add(new Tile("Chance to bust trip bankroll")); tTripBest = tiles.Add(new Tile("Good trip (90th pct.)")); tTripWorst = tiles.Add(new Tile("Bad trip (10th pct.)"));

            var rc = Stack(right, new Card("Risk of ruin vs. bankroll"));
            rorChart = rc.AddRow(ChartUtil.Create("Bankroll ($)", "Risk of ruin (%)", 260));
            var pcCard = Stack(right, new Card("Expected result over time (±1 and ±2 SD)"));
            pathChart = pcCard.AddRow(ChartUtil.Create("Hours played", "Result ($)", 300));

            Load();
            EventHandler h = (o, e) => Save();
            bankroll.ValueChanged += h; tripHours.ValueChanged += h; tripBank.ValueChanged += h;
            State.BettingChanged += (o, e) => { if (!loading) { Load(); if (Visible) Refresh2(); } };
            State.SimChanged += (o, e) => { if (Visible) Refresh2(); };
            State.Reloaded += (o, e) => { Load(); Refresh2(); };
        }


        void Load()
        {
            loading = true;
            bankroll.Value = (decimal)Math.Max(1, Math.Min(1000000000, State.Bankroll));
            tripHours.Value = (decimal)Math.Max(1, Math.Min(100000, State.TripHours));
            tripBank.Value = (decimal)Math.Max(1, Math.Min(1000000000, State.TripBankroll));
            loading = false;
        }

        void Save()
        {
            if (loading) return;
            State.Bankroll = (double)bankroll.Value;
            State.TripHours = (double)tripHours.Value;
            State.TripBankroll = (double)tripBank.Value;
            loading = true;
            State.NotifyBetting();
            loading = false;
            Refresh2();
        }

        public override void OnActivated() { Load(); Refresh2(); }

        void Refresh2() { Batch(Refresh2Core); }


        void Refresh2Core()
        {
            var a = State.Result;
            needed.Rows.Clear(); pct.Rows.Clear();
            ChartUtil.Clear(rorChart); ChartUtil.Clear(pathChart);
            if (a == null || !a.Valid)
            {
                banner.Show(State.Sim == null ? "No simulation yet — press Run simulation (F5)." : "The current ramp never bets.", false);
                foreach (var t in new[] { tRor, tTripExp, tTripLose, tTripRuin, tTripBest, tTripWorst }) t.Set("—", "");
                return;
            }
            if (State.SimStale) banner.Show("⚠  Settings changed since the last simulation; figures are for the previous settings.", true);
            else banner.Visible = false;

            double unit = State.Unit;
            double mu = a.EV * unit, vr = a.Var * unit * unit;          // per observed round, dollars
            double n = State.TripHours * a.RoundsPerHour;
            double exp = mu * n, sd = Math.Sqrt(vr * n);
            double maxBet = State.Ramp.MaxBet() * unit;   // all hands of the top bet

            tRor.Set(Theme.Pct(a.ROR, a.ROR < 0.001 ? 4 : 2), "with " + Theme.Money0(State.Bankroll), a.ROR > 0.2 ? Theme.Bad : (a.ROR < 0.05 ? Theme.Good : Theme.Text));
            tTripExp.Set(Theme.Money0(exp), "± " + Theme.Money0(sd) + " (1 SD) over " + Theme.F(State.TripHours, "#,0") + " h", exp >= 0 ? Theme.Good : Theme.Bad);
            tTripLose.Set(Theme.Pct(Analysis.ProbLoss(mu, vr, n), 1), "behind at the end of the trip");
            double tr = Analysis.TripRuin(mu, vr, State.TripBankroll, n);
            tTripRuin.Set(Theme.Pct(tr, tr < 0.001 ? 3 : 1), "trip bankroll " + Theme.Money0(State.TripBankroll), tr > 0.2 ? Theme.Bad : Theme.Text);
            tTripBest.Set(Theme.Money0(exp + 1.2816 * sd), "1 trip in 10 is better", Theme.Good);
            tTripWorst.Set(Theme.Money0(exp - 1.2816 * sd), "1 trip in 10 is worse", exp - 1.2816 * sd >= 0 ? Theme.Good : Theme.Bad);

            foreach (double t in Targets)
            {
                double b = a.BankrollForRor(t);
                needed.Rows.Add(Theme.Pct(t, t < 0.01 ? 1 : (t == 0.135 ? 1 : 0)), Theme.Money0(b), Theme.F(b / unit, "#,0"), maxBet > 0 ? Theme.F(b / maxBet, "#,0") : "—");
            }
            GridUtil.FitHeight(needed, 12);
            foreach (double p in Percentiles)
            {
                double z = Analysis.PhiInv(p);
                double v = exp + z * sd;
                int i = pct.Rows.Add(Theme.Pct(p, 0), Theme.Money0(v), Theme.Money(v / State.TripHours));
                pct.Rows[i].Cells[1].Style.ForeColor = v >= 0 ? Theme.Good : Theme.Bad;
            }
            GridUtil.FitHeight(pct, 12);

            // ROR curve
            if (a.EV > 0)
            {
                double b13 = a.BankrollForRor(0.001);
                var s = ChartUtil.Add(rorChart, "Lifetime ROR", SeriesChartType.Line, ChartUtil.Series1);
                for (int k = 1; k <= 120; k++)
                {
                    double b = b13 * k / 120.0;
                    s.Points.AddXY(b, 100 * Math.Exp(-2 * a.EV * (b / unit) / a.Var));
                }
                var cur = ChartUtil.Add(rorChart, "Your bankroll", SeriesChartType.Point, ChartUtil.Series3);
                cur.MarkerSize = Theme.S(9); cur.MarkerStyle = MarkerStyle.Circle;
                if (State.Bankroll <= b13) cur.Points.AddXY(State.Bankroll, 100 * a.ROR);
                var ax = rorChart.ChartAreas[0];
                ChartUtil.NiceX(rorChart, 0, b13, 6);
                ax.AxisX.LabelStyle.Format = "$#,0"; ax.AxisY.Minimum = 0; ax.AxisY.Maximum = 100; ax.AxisY.Interval = 20;
                ChartUtil.Message(rorChart, "");
            }
            else ChartUtil.Message(rorChart, "Negative expectation: ruin is certain in the long run.");

            // Path bands
            double horizon = Math.Max(State.TripHours, Math.Min(5000, a.HoursN0 > 0 && !double.IsInfinity(a.HoursN0) ? a.HoursN0 * 1.2 : 200));
            var b2 = ChartUtil.Add(pathChart, "±2 SD", SeriesChartType.Range, Color.FromArgb(55, ChartUtil.Series2));
            var b1 = ChartUtil.Add(pathChart, "±1 SD", SeriesChartType.Range, Color.FromArgb(90, ChartUtil.Series2));
            var ev = ChartUtil.Add(pathChart, "Expected", SeriesChartType.Line, ChartUtil.Series1);
            var zero = ChartUtil.Add(pathChart, "Break-even", SeriesChartType.Line, Theme.Muted);
            zero.BorderDashStyle = ChartDashStyle.Dash; zero.BorderWidth = 1;
            double rphr = a.RoundsPerHour;
            for (int k = 0; k <= 100; k++)
            {
                double h = horizon * k / 100.0;
                double m = mu * h * rphr, sdv = Math.Sqrt(vr * h * rphr);
                b2.Points.AddXY(h, m - 2 * sdv, m + 2 * sdv);
                b1.Points.AddXY(h, m - sdv, m + sdv);
                ev.Points.AddXY(h, m);
                zero.Points.AddXY(h, 0);
            }
            var pa = pathChart.ChartAreas[0];
            ChartUtil.NiceX(pathChart, 0, horizon, 8);
            pa.AxisY.LabelStyle.Format = "$#,0";
            pa.AxisX.LabelStyle.Format = "#,0";
        }
    }
}
