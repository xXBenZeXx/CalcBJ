using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.Threading;
using System.Windows.Forms;
using System.Windows.Forms.DataVisualization.Charting;
using CalcBJ.Engine;

namespace CalcBJ.App
{
    /// <summary>Re-simulates the current game over a range of one parameter (or re-optimises the ramp per max bet).</summary>
    internal sealed class SweepPage : PageBase
    {
        readonly ComboBox param, rounds, metric;
        readonly NumericUpDown from, to, step;
        readonly Button run, stop;
        readonly Label status, note;
        readonly DataGridView grid;
        readonly Chart chart;
        readonly List<Point2> points = new List<Point2>();
        volatile bool cancel;
        Thread worker;
        static readonly long[] RoundOptions = { 10000000, 25000000, 50000000, 100000000, 200000000 };
        static readonly string[] IndexNames = { "Basic", "I18", "I18+Fab4", "Full" };

        sealed class Point2
        {
            public string Label;
            public double X;
            public AnalysisResult R;
        }

        public SweepPage(AppState s)
            : base(s, "Sensitivity sweep", "How win rate, SCORE and risk change as one parameter varies — everything else stays as set.", 38, 62)
        {
            var left = Column(0);
            var right = Column(1);

            var c = Stack(left, new Card("Sweep"));
            var g = c.AddRow(new FieldGrid());
            param = g.Add("Parameter", Theme.Combo(250, "Penetration (%)", "Number of decks", "Spots per round", "Index set", "Max bet, optimal ramp (no re-sim)"));
            from = g.Add("From", Theme.Num(-1000, 100000, 60, 1, 1, 90));
            to = g.Add("To", Theme.Num(-1000, 100000, 90, 1, 1, 90));
            step = g.Add("Step", Theme.Num(0.1m, 100000, 5, 1, 1, 90));
            rounds = g.Add("Rounds per point", Theme.Combo(150, "10M", "25M", "50M", "100M", "200M"));
            rounds.SelectedIndex = 2;
            note = c.AddRow(Theme.Wrap("", 420));
            var bs = c.AddRow(new Strip());
            run = bs.Add(Theme.Primary("Run sweep"));
            stop = bs.Add(Theme.Secondary("Stop"));
            status = c.AddRow(Theme.Wrap("", 420));

            var rc = Stack(left, new Card("Points"));
            grid = rc.AddRow(new DataGridView());
            Theme.StyleGrid(grid);
            grid.ReadOnly = true;
            grid.Columns.Add(Theme.TextCol("Value", 72, true, true));
            grid.Columns.Add(Theme.TextCol("Win/h", 72, true, true));
            grid.Columns.Add(Theme.TextCol("Edge", 64, true, true));
            grid.Columns.Add(Theme.TextCol("SCORE", 56, true, true));
            grid.Columns.Add(Theme.TextCol("N0 (h)", 60, true, true));
            grid.Columns.Add(Theme.TextCol("ROR", 56, true, true));
            grid.Columns[5].AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill;
            grid.Dock = DockStyle.Fill;
            GridUtil.NoSelection(grid);
            GridUtil.FitHeight(grid, 4);

            var cc = Stack(right, new Card("Chart"));
            var cg = cc.AddRow(new FieldGrid());
            metric = cg.Add("Show", Theme.Combo(200, "SCORE", "Win rate per hour", "Player edge", "Risk of ruin", "N0 (hours)", "Std. deviation per hour"));
            metric.SelectedIndex = 0;
            chart = cc.AddRow(ChartUtil.Create("", "", 520));

            param.SelectedIndexChanged += (o, e) => Defaults();
            metric.SelectedIndexChanged += (o, e) => Draw();
            run.Click += (o, e) => Start();
            stop.Click += (o, e) => { cancel = true; };
            param.SelectedIndex = 0;
        }

        void Defaults()
        {
            switch (param.SelectedIndex)
            {
                case 0: Set(60, 90, 5, 1); note.Text = "Each point is a fresh simulation at that penetration."; break;
                case 1: Set(1, 8, 1, 0); note.Text = "Each point is a fresh simulation with that many decks (index plays are regenerated)."; break;
                case 2: Set(1, 7, 1, 0); note.Text = "More spots means fewer rounds per hour (auto table) and fewer rounds per shoe."; break;
                case 3: Set(0, 3, 1, 0); note.Text = "0 = basic strategy, 1 = Illustrious 18, 2 = I18 + Fab 4, 3 = full indices."; break;
                default: Set(2, 24, 2, 0); note.Text = "Uses the current simulation; for each maximum bet the SCORE-optimal ramp (min 1 unit, whole units) is found."; break;
            }
        }

        void Set(decimal a, decimal b, decimal st, int dec)
        {
            from.DecimalPlaces = to.DecimalPlaces = step.DecimalPlaces = dec;
            from.Value = a; to.Value = b; step.Value = st;
        }

        void Start()
        {
            if (worker != null && worker.IsAlive) return;
            int p = param.SelectedIndex;
            double a = (double)from.Value, b = (double)to.Value, st = (double)step.Value;
            if (st <= 0 || b < a) { status.Text = "Check the range."; return; }
            if (p == 4 && State.Sim == null) { status.Text = "Run a simulation first."; return; }
            var values = new List<double>();
            for (double v = a; v <= b + 1e-9 && values.Count < 60; v += st) values.Add(Math.Round(v, 4));
            long n = RoundOptions[Math.Max(0, rounds.SelectedIndex)];
            var rules = State.Rules.Clone();
            var cs = State.Count.Clone();
            var sc = CloneStrategy(State.Strategy);
            var ramp = State.Ramp.Clone();
            int preset = State.RampPreset;
            int spots = State.Spots;
            double unit = State.Unit, rph = State.RoundsPerHour, bank = State.Bankroll;
            bool autoRph = State.AutoRoundsPerHour;
            int threads = State.Threads;
            var sim = State.Sim;
            points.Clear();
            grid.Rows.Clear();
            cancel = false;
            run.Enabled = false;
            worker = new Thread(() =>
            {
                try
                {
                    foreach (double v in values)
                    {
                        if (cancel) break;
                        var pt = new Point2();
                        pt.X = v;
                        if (p == 4)
                        {
                            var os = new OptimizerSettings();
                            os.MinUnits = 1; os.MaxUnits = Math.Max(1, v); os.Increment = 1; os.MaxHands = 1;
                            var r = Analysis.Optimize(sim, ramp, os);
                            pt.R = Analysis.Evaluate(sim, r, unit, rph, bank);
                            pt.Label = "1–" + v.ToString("0.##", CultureInfo.InvariantCulture);
                        }
                        else
                        {
                            var r2 = rules.Clone();
                            var sc2 = CloneStrategy(sc);
                            var ramp2 = ramp;
                            int sp = spots;
                            double rph2 = rph;
                            if (p == 0) { r2.Penetration = v / 100.0; pt.Label = v.ToString("0.#", CultureInfo.InvariantCulture) + "%"; }
                            else if (p == 1)
                            {
                                r2.Decks = Math.Max(1, Math.Min(8, (int)v));
                                pt.Label = r2.Decks + "D";
                                if (AppState.DependsOnDecks(cs))
                                {
                                    // As on the Game page: the index range and the ramp move with the number of decks.
                                    int ilo, ihi;
                                    AppState.IndexRangeFor(cs, r2, out ilo, out ihi);
                                    sc2.IndexMin = ilo; sc2.IndexMax = ihi;
                                    ramp2 = AppState.RampForRules(cs, rules, r2, ramp, preset);
                                }
                            }
                            else if (p == 2) { sp = Math.Max(1, Math.Min(7, (int)v)); if (autoRph) rph2 = AppState.DefaultRoundsPerHour(sp); pt.Label = sp + " spots"; }
                            else { sc2.Mode = (IndexMode)Math.Max(0, Math.Min(3, (int)v)); pt.Label = IndexNames[(int)sc2.Mode]; }
                            var table = StrategyBuilder.Build(r2, cs, sc2);
                            var su = new SimSetup();
                            su.Rules = r2; su.Count = cs; su.Table = table; su.Spots = sp; su.Rounds = n;
                            su.Threads = threads; su.Seed = (ulong)DateTime.UtcNow.Ticks;
                            var simr = new Simulator(su);
                            var done = new ManualResetEvent(false);
                            simr.Finished += (o, e) => done.Set();
                            simr.Start();
                            while (!done.WaitOne(100)) if (cancel) simr.Stop();
                            if (simr.Error != null) throw simr.Error;
                            var d = simr.Snapshot();
                            if (d.Rounds == 0) break;
                            d.Key = AppState.SimKeyOf(r2, cs, sc2, sp);
                            d.RulesText = r2.Describe();
                            d.CountText = AppState.CountTextOf(cs);
                            d.StrategyText = AppState.StrategyTextOf(sc2);
                            d.BasicEV = table.BasicResult.PlayerEV;
                            d.Metric = cs.Metric;
                            d.Threads = threads;
                            if (!cancel) State.Remember(d);
                            pt.R = Analysis.Evaluate(d, ramp2, unit, rph2, bank);
                        }
                        lock (points) points.Add(pt);
                        BeginInvoke(new Action(() => AddRow(pt, values.Count)));
                    }
                }
                catch (Exception ex)
                {
                    BeginInvoke(new Action(() => status.Text = "Sweep failed: " + ex.Message));
                }
                finally
                {
                    BeginInvoke(new Action(() => { run.Enabled = true; if (cancel) status.Text = "Stopped after " + points.Count + " point(s)."; }));
                }
            });
            worker.IsBackground = true;
            worker.Start();
            status.Text = "Running " + values.Count + " point(s)…";
        }

        static StrategyConfig CloneStrategy(StrategyConfig s)
        {
            var c = new StrategyConfig();
            c.Mode = s.Mode; c.IndexMin = s.IndexMin; c.IndexMax = s.IndexMax;
            c.Overrides = new List<IndexOverride>(s.Overrides);
            return c;
        }

        void AddRow(Point2 p, int total) { Batch(() => AddRowCore(p, total)); }

        void AddRowCore(Point2 p, int total)
        {
            var r = p.R;
            if (r == null || !r.Valid) grid.Rows.Add(p.Label, "—", "—", "—", "—", "—");
            else grid.Rows.Add(p.Label, Theme.Money(r.WinPerHour), Theme.SignedPct(r.EdgePct / 100, 2), Theme.F(r.Score, "0.0"),
                r.EV > 0 ? Theme.F(r.HoursN0, "#,0") : "—", Theme.Pct(r.ROR, 1));
            GridUtil.FitHeight(grid, 40);
            status.Text = "Point " + grid.Rows.Count + " of " + total + " done.";
            Draw();
        }

        void Draw()
        {
            ChartUtil.Clear(chart);
            List<Point2> copy;
            lock (points) copy = new List<Point2>(points);
            if (copy.Count == 0) return;
            string name = Convert.ToString(metric.SelectedItem);
            var s = ChartUtil.Add(chart, name, SeriesChartType.Line, ChartUtil.Series1);
            s.MarkerStyle = MarkerStyle.Circle;
            s.MarkerSize = Theme.S(7);
            for (int i = 0; i < copy.Count; i++)
            {
                var r = copy[i].R;
                if (r == null || !r.Valid) continue;
                double y;
                switch (metric.SelectedIndex)
                {
                    case 1: y = r.WinPerHour; break;
                    case 2: y = r.EdgePct; break;
                    case 3: y = r.ROR * 100; break;
                    case 4: y = r.EV > 0 ? r.HoursN0 : double.NaN; break;
                    case 5: y = r.SdPerHour; break;
                    default: y = r.Score; break;
                }
                if (double.IsNaN(y) || double.IsInfinity(y)) continue;
                int k = s.Points.AddXY(i + 1, y);
                s.Points[k].AxisLabel = copy[i].Label;
                s.Points[k].Label = y.ToString(metric.SelectedIndex == 3 || metric.SelectedIndex == 2 ? "0.00" : "#,0.#", CultureInfo.InvariantCulture);
                s.Points[k].LabelForeColor = Theme.Muted;
                s.Points[k].Font = Theme.Small;
            }
            var ax = chart.ChartAreas[0];
            ax.AxisX.Interval = 1;
            ax.AxisX.MajorGrid.Enabled = false;
            ax.AxisX.Minimum = 0.5;
            ax.AxisX.Maximum = copy.Count + 0.5;
            ax.AxisX.IntervalOffset = 0.5;
            ax.AxisX.LabelStyle.Angle = copy.Count > 10 ? -45 : 0;
            ax.AxisY.Title = name;
            ax.AxisX.Title = Convert.ToString(param.SelectedItem);
        }
    }
}
