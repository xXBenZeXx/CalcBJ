using System;
using System.Drawing;
using System.Globalization;
using System.Windows.Forms;
using CalcBJ.Engine;

namespace CalcBJ.App
{
    internal sealed class SimPage : PageBase
    {
        readonly MainForm form;
        readonly ComboBox rounds;
        readonly NumericUpDown threads;
        readonly CheckBox autoRun;
        readonly Label setupInfo, progressInfo;
        readonly Tile tRounds, tSpeed, tFlat, tSd, tCut, tShoe;
        readonly DataGridView bins;
        readonly Banner stale;
        readonly Button run;
        static readonly long[] RoundOptions = { 10000000, 25000000, 50000000, 100000000, 200000000, 500000000, 1000000000, 2000000000, 5000000000, 10000000000 };

        public SimPage(AppState s, MainForm f)
            : base(s, "Simulation", "Simulate the game once; every bet ramp is then evaluated instantly from the per-count results.", 42, 58)
        {
            form = f;
            var left = Column(0);
            var right = Column(1);

            var setup = Stack(left, new Card("Simulation settings"));
            var g = setup.AddRow(new FieldGrid());
            var names = new string[RoundOptions.Length];
            for (int i = 0; i < names.Length; i++) names[i] = Theme.Count(RoundOptions[i]) + " rounds";
            rounds = g.Add("Rounds to simulate", Theme.Combo(170, names));
            threads = g.Add("CPU threads", Theme.Num(1, 256, Environment.ProcessorCount, 0, 1, 70));
            autoRun = g.AddFull(Theme.Check("Re-run automatically when settings change"));
            g.AddFull(Theme.Wrap("Off by default: the simulation only runs when you press Run (F5). Settings you already simulated still show their results straight away.", 440));
            var bs = setup.AddRow(new Strip());
            run = bs.Add(Theme.Primary("▶  Run simulation  (F5)"));
            run.Click += (o, e) => form.StartSim();
            var stop = bs.Add(Theme.Secondary("Stop  (Esc)"));
            stop.Click += (o, e) => form.StopSim();
            var discard = bs.Add(Theme.Secondary("Discard results & re-run"));
            discard.Click += (o, e) => form.DiscardAndRun();
            setupInfo = setup.AddRow(Theme.Wrap("", 440));
            progressInfo = setup.AddRow(Theme.Wrap("", 440));

            var how = Stack(left, new Card("How the simulator works"));
            how.AddRow(Theme.Wrap(
                "Every round is dealt from a shuffled shoe to all spots; the dealer peeks (or not) as the rules say, and "
                + "every player decision uses your strategy at the running/true count at that moment, counting every card "
                + "that has been exposed. Results for your seat are accumulated separately for each count value, together "
                + "with the covariance to the neighbouring seat. That is all the bet-ramp, risk and optimiser pages need, so "
                + "they never have to re-simulate.\n\nPlayer results are tallied exactly (in 1/20 units). A modern 8-core CPU "
                + "runs on the order of 100 million heads-up rounds per second; more spots per round take proportionally longer.", 440));

            stale = Stack(right, new Banner());
            var sum = Stack(right, new Card("Results"));
            var tiles = sum.AddRow(new TileGrid(3));
            tRounds = tiles.Add(new Tile("Rounds simulated")); tSpeed = tiles.Add(new Tile("Speed"));
            tShoe = tiles.Add(new Tile("Rounds per shoe"));
            tFlat = tiles.Add(new Tile("Flat-bet EV")); tSd = tiles.Add(new Tile("SD per hand"));
            tCut = tiles.Add(new Tile("Theoretical EV (CA)"));

            var bc = Stack(right, new Card("Results by count (one hand, flat bet)"));
            bins = bc.AddRow(new DataGridView());
            Theme.StyleGrid(bins);
            bins.ReadOnly = true;
            bins.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
            bins.Columns.Add(Theme.TextCol("Count", 70, true, true));
            bins.Columns.Add(Theme.TextCol("Rounds", 100, true, true));
            bins.Columns.Add(Theme.TextCol("Frequency", 84, true, true));
            bins.Columns.Add(Theme.TextCol("EV / hand", 84, true, true));
            bins.Columns.Add(Theme.TextCol("± 1 SE", 70, true, true));
            bins.Columns.Add(Theme.TextCol("SD", 64, true, true));
            bins.Columns.Add(Theme.TextCol("2-hand corr.", 90, true, true));
            bins.Columns[6].AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill;
            bins.Dock = DockStyle.Fill;
            bins.Height = Theme.S(520);
            GridUtil.NoSelection(bins);

            Load();
            rounds.SelectedIndexChanged += (o, e) => { int i = rounds.SelectedIndex; if (i >= 0) State.SimRounds = RoundOptions[i]; Refresh2(); };
            threads.ValueChanged += (o, e) => State.Threads = (int)threads.Value;
            autoRun.CheckedChanged += (o, e) => State.AutoRun = autoRun.Checked;
            State.SimChanged += (o, e) => { if (Visible) Refresh2(); };
            State.ConfigChanged += (o, e) => { if (Visible) Refresh2(); };
            State.Reloaded += (o, e) => { Load(); Refresh2(); };
        }

        void Load()
        {
            int idx = Array.IndexOf(RoundOptions, State.SimRounds);
            if (idx < 0) { idx = 4; State.SimRounds = RoundOptions[idx]; }
            rounds.SelectedIndex = idx;
            threads.Value = Math.Max(1, Math.Min(256, State.Threads));
            autoRun.Checked = State.AutoRun;
        }

        public override void OnActivated() { Refresh2(); }

        void Refresh2() { Batch(Refresh2Core); }


        void Refresh2Core()
        {
            setupInfo.Text = "Rules: " + State.Rules.Describe() + "\nCount: " + State.CountText() + "\nPlay: " + State.StrategyText()
                + "\nTable: " + State.Spots + " spot(s), cut card at " + State.Rules.CutCard(State.Spots) + " of " + State.Rules.TotalCards + " cards";
            var sim = form.CurrentSim;
            var d = State.Sim;
            if (sim != null && sim.IsRunning)
                progressInfo.Text = string.Format(CultureInfo.InvariantCulture, "Running: {0:0.0}% of {1} rounds", 100.0 * sim.Done / sim.Target, Theme.Count(sim.Target));
            else progressInfo.Text = d == null ? "No results yet." : "Finished " + d.Created.ToString("HH:mm:ss", CultureInfo.InvariantCulture) + ".";
            progressInfo.Text += "\nResults are kept for " + State.CacheCount + " configuration(s): returning to one of them is instant, "
                + "and Run adds rounds to the results already held.";

            if (State.SimStale) stale.Show("⚠  Settings have changed since this simulation. Results below are for: " + (d == null ? "" : d.RulesText + " · " + d.CountText + " · " + d.StrategyText), true);
            else stale.Visible = false;

            bins.Rows.Clear();
            if (d == null || d.Rounds == 0)
            {
                tRounds.Set("—", ""); tSpeed.Set("—", ""); tFlat.Set("—", ""); tSd.Set("—", ""); tCut.Set("—", ""); tShoe.Set("—", "");
                return;
            }
            tRounds.Set(Theme.Count(d.Rounds), d.Shoes.ToString("#,0", CultureInfo.InvariantCulture) + " shoes");
            tSpeed.Set(Theme.F(d.Seconds > 0 ? d.Rounds / d.Seconds / 1e6 : 0, "0.0") + "M/s", Theme.F(d.Seconds, "0.0") + " s on " + d.Threads + " threads");
            tShoe.Set(Theme.F(d.Shoes > 0 ? (double)d.Rounds / d.Shoes : 0, "0.0"), d.Emergency > 0 ? d.Emergency + " reshuffle fallbacks" : "per shuffle");
            var flat = Analysis.Evaluate(d, BetRamp.Create(0, 0, 1), 1, 100, 1000);
            tFlat.Set(Theme.SignedPct(flat.EV, 3), "± " + Theme.Pct(flat.SeEV, 3) + " (1 SE), with your indices", flat.EV >= 0 ? Theme.Good : Theme.Bad);
            tSd.Set(Theme.F(flat.SD, "0.000"), "units per round, flat bet");
            tCut.Set(Theme.SignedPct(d.BasicEV, 3), "basic strategy off the top");

            var stats = Analysis.Bins(d);
            int lo = int.MaxValue, hi = int.MinValue;
            foreach (var b in stats) if (b.Freq >= 0.00005) { lo = Math.Min(lo, b.Value); hi = Math.Max(hi, b.Value); }
            if (lo > hi) return;
            string m = d.Metric == CountMetric.TrueCount ? "TC " : "RC ";
            foreach (var b in stats)
            {
                if (b.Value < lo || b.Value > hi || b.N == 0) continue;
                double sd = Math.Sqrt(Math.Max(0, b.Var));
                double corr = b.Var > 0 ? (b.Cov - b.Mean * b.Mean) / b.Var : 0;
                int i = bins.Rows.Add((b.Value > 0 ? "+" : "") + b.Value.ToString(CultureInfo.InvariantCulture),
                    b.N.ToString("#,0", CultureInfo.InvariantCulture), Theme.Pct(b.Freq, 3), Theme.SignedPct(b.Mean, 3),
                    Theme.Pct(Math.Sqrt(b.MeanVar), 3), Theme.F(sd, "0.000"), b.MeasuredCov ? Theme.F(corr, "0.000") : "(assumed)");
                bins.Rows[i].Cells[3].Style.ForeColor = b.Mean >= 0 ? Theme.Good : Theme.Bad;
            }
            GridUtil.FitHeight(bins, 30);
        }
    }
}
