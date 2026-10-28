using System;
using System.Drawing;
using System.Globalization;
using System.Windows.Forms;
using CalcBJ.Engine;

namespace CalcBJ.App
{
    internal sealed class ResultsPage : PageBase
    {
        readonly NumericUpDown unit, bankroll, rph, rampMin, rampMax;
        readonly ComboBox preset;
        readonly DataGridView ramp, contrib;
        readonly Banner banner;
        readonly Tile tWin, tSd, tEdge, tScore, tN0, tRor, tBank, tKelly, tCe, tSpread, tDouble, tHouse;
        bool loading;
        int builtMin = int.MinValue, builtMax = int.MinValue;

        public ResultsPage(AppState s)
            : base(s, "Bet ramp & results", "Set your bets by count; win rate, risk and SCORE update instantly from the simulation.", 40, 60)
        {
            var left = Column(0);
            var right = Column(1);

            var bet = Stack(left, new Card("Betting"));
            var g = bet.AddRow(new FieldGrid());
            unit = g.Add("Betting unit ($)", Theme.Num(1, 1000000, 25, 2, 5, 110));
            bankroll = g.Add("Bankroll ($)", Theme.Num(1, 1000000000, 25000, 0, 1000, 110));
            rph = g.Add("Rounds per hour", Theme.Num(10, 400, 105, 0, 5, 110));
            var rs = g.Add("Ramp rows", new Strip());
            rampMin = rs.Add(Theme.Num(-100, 99, -3, 0, 1, 64));
            rs.Add(Theme.Lbl("to"));
            rampMax = rs.Add(Theme.Num(-99, 100, 8, 0, 1, 64));

            var rc = Stack(left, new Card("Bet ramp"));
            var tools = rc.AddRow(new Strip(true));
            preset = tools.Add(Theme.Combo(220, RampPresets.Names));
            var opt = tools.Add(Theme.Primary("Optimise…"));
            var gen = tools.Add(Theme.Secondary("Linear…"));
            rc.AddRow(Theme.Wrap("Edit Units and Hands directly. 0 units = sit out (wong out); 2–3 hands use the "
                + "covariance measured between neighbouring seats.", 470));
            ramp = rc.AddRow(new DataGridView());
            Theme.StyleGrid(ramp);
            ramp.EditMode = DataGridViewEditMode.EditOnKeystrokeOrF2;
            ramp.Columns.Add(Theme.TextCol("Count", 64, true, true));
            ramp.Columns.Add(Theme.TextCol("Freq.", 66, true, true));
            ramp.Columns.Add(Theme.TextCol("EV/hand", 72, true, true));
            var uc = Theme.TextCol("Units", 64, true, false);
            uc.DefaultCellStyle.BackColor = Theme.AccentSoft;
            uc.DefaultCellStyle.Font = Theme.BodyBold;
            ramp.Columns.Add(uc);
            var hc = Theme.TextCol("Hands", 56, true, false);
            hc.DefaultCellStyle.BackColor = Theme.AccentSoft;
            ramp.Columns.Add(hc);
            ramp.Columns.Add(Theme.TextCol("Bet", 80, true, true));
            ramp.Columns[5].AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill;
            ramp.Dock = DockStyle.Fill;
            ramp.CellEndEdit += (o, e) => SaveRampCell(e.RowIndex, e.ColumnIndex);

            banner = Stack(right, new Banner());
            var res = Stack(right, new Card("Results"));
            var tiles = res.AddRow(new TileGrid(3));
            tWin = tiles.Add(new Tile("Win rate per hour")); tSd = tiles.Add(new Tile("Std. deviation per hour")); tEdge = tiles.Add(new Tile("Player edge"));
            tScore = tiles.Add(new Tile("SCORE")); tN0 = tiles.Add(new Tile("N0 (long run)")); tRor = tiles.Add(new Tile("Risk of ruin"));
            tBank = tiles.Add(new Tile("Bankroll for 5% ROR")); tKelly = tiles.Add(new Tile("Kelly-optimal unit")); tCe = tiles.Add(new Tile("Certainty equivalent"));
            tSpread = tiles.Add(new Tile("Spread & coverage")); tDouble = tiles.Add(new Tile("Hours to double bankroll")); tHouse = tiles.Add(new Tile("Basic strategy edge"));

            var cc = Stack(right, new Card("Where the money comes from"));
            contrib = cc.AddRow(new DataGridView());
            Theme.StyleGrid(contrib);
            contrib.ReadOnly = true;
            contrib.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
            contrib.Columns.Add(Theme.TextCol("Count", 64, true, true));
            contrib.Columns.Add(Theme.TextCol("Freq.", 70, true, true));
            contrib.Columns.Add(Theme.TextCol("EV/hand", 76, true, true));
            contrib.Columns.Add(Theme.TextCol("Wager", 84, true, true));
            contrib.Columns.Add(Theme.TextCol("Win / 100 rds", 100, true, true));
            contrib.Columns.Add(Theme.TextCol("Share of win", 90, true, true));
            contrib.Columns.Add(Theme.TextCol("Share of variance", 110, true, true));
            contrib.Columns[6].AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill;
            contrib.Dock = DockStyle.Fill;
            GridUtil.NoSelection(contrib);

            LoadInputs();
            EventHandler h = (o, e) => SaveInputs();
            unit.ValueChanged += h; bankroll.ValueChanged += h; rph.ValueChanged += h;
            rampMin.ValueChanged += (o, e) => SaveRange();
            rampMax.ValueChanged += (o, e) => SaveRange();
            preset.SelectedIndexChanged += (o, e) =>
            {
                if (loading || preset.SelectedIndex < 0 || preset.SelectedIndex == RampPresets.Custom) return;
                State.ApplyPreset(preset.SelectedIndex);
                State.NotifyBetting();
            };
            opt.Click += (o, e) => Optimise();
            gen.Click += (o, e) =>
            {
                using (var d = new GenerateDialog(State.Count.Metric == CountMetric.RunningCount))
                    if (d.ShowDialog(FindForm()) == DialogResult.OK) { d.Apply(State.Ramp); MarkCustom(); State.NotifyBetting(); }
            };
            Theme.Tip(tWin, "Expected profit per hour at your unit size and rounds per hour (rounds you sit out still take time).");
            Theme.Tip(tSd, "Standard deviation of one hour's result. After h hours the SD is this × √h.");
            Theme.Tip(tEdge, "Win rate divided by the average initial amount wagered per round played.");
            Theme.Tip(tScore, "Win rate per 100 rounds for a $10,000 bankroll bet at full Kelly with this ramp shape (= DI²). Compares games independent of bet size.");
            Theme.Tip(tN0, "Rounds needed until the expected win equals one standard deviation: (SD/EV)². After N0 rounds you are ahead about 84% of the time.");
            Theme.Tip(tRor, "Chance of eventually losing the whole bankroll if you never change your unit: exp(−2·EV·B/variance).");
            Theme.Tip(tBank, "Bankroll that gives a 5% (13.5%, 1%) lifetime risk of ruin with this ramp and unit.");
            Theme.Tip(tKelly, "Unit size that maximises long-run bankroll growth for this ramp shape and your bankroll.");
            Theme.Tip(tCe, "Risk-adjusted value of the game for your bankroll (log utility): win rate − variance/(2·bankroll), per hour.");
            State.BettingChanged += (o, e) => { if (Visible) Refresh2(); };
            State.SimChanged += (o, e) => { if (Visible) Refresh2(); };
            State.ConfigChanged += (o, e) => { if (!loading) { LoadInputs(); if (Visible) Refresh2(); } };
            State.Reloaded += (o, e) => { LoadInputs(); Refresh2(); };
        }

        void LoadInputs()
        {
            loading = true;
            unit.Value = (decimal)Math.Max(1, Math.Min(1000000, State.Unit));
            bankroll.Value = (decimal)Math.Max(1, Math.Min(1000000000, State.Bankroll));
            rph.Value = (decimal)Math.Max(10, Math.Min(400, State.RoundsPerHour));
            rampMin.Value = Math.Max(rampMin.Minimum, Math.Min(rampMin.Maximum, State.Ramp.Min));
            rampMax.Value = Math.Max(rampMax.Minimum, Math.Min(rampMax.Maximum, State.Ramp.Max));
            preset.SelectedIndex = State.RampPreset < 0 ? RampPresets.Custom : Math.Min(RampPresets.Custom, State.RampPreset);
            loading = false;
        }

        void SaveInputs()
        {
            if (loading) return;
            State.Unit = (double)unit.Value;
            State.Bankroll = (double)bankroll.Value;
            if (Math.Abs(State.RoundsPerHour - (double)rph.Value) > 1e-9)
            {
                State.RoundsPerHour = (double)rph.Value;
                State.AutoRoundsPerHour = false;
            }
            State.NotifyBetting();
        }

        void SaveRange()
        {
            if (loading) return;
            int lo = (int)rampMin.Value, hi = (int)rampMax.Value;
            if (hi <= lo) return;
            State.Ramp = State.Ramp.Resize(lo, hi);
            State.NotifyBetting();
        }

        void SaveRampCell(int row, int col)
        {
            if (row < 0 || row >= State.Ramp.Rows) return;
            string text = Convert.ToString(ramp.Rows[row].Cells[col].Value, CultureInfo.InvariantCulture);
            double v;
            if (col == 3 && Theme.TryParse(text, out v)) State.Ramp.Units[row] = Math.Max(0, Math.Min(100000, v));
            if (col == 4 && Theme.TryParse(text, out v)) State.Ramp.Hands[row] = Math.Max(1, Math.Min(3, (int)Math.Round(v)));
            MarkCustom();
            State.NotifyBetting();
        }

        void MarkCustom()
        {
            State.RampPreset = RampPresets.Custom;
            loading = true;
            preset.SelectedIndex = RampPresets.Custom;
            loading = false;
        }

        void Optimise()
        {
            if (State.Sim == null) { MessageBox.Show(FindForm(), "Run a simulation first.", "CalcBJ"); return; }
            double bu = State.Bankroll / State.Unit;
            using (var d = new OptimizeDialog(State.Ramp.MaxUnits(), bu))
            {
                if (d.ShowDialog(FindForm()) != DialogResult.OK) return;
                State.Ramp = Analysis.Optimize(State.Sim, State.Ramp, d.Settings(bu));
                MarkCustom();
                State.NotifyBetting();
            }
        }

        public override void OnActivated() { LoadInputs(); Refresh2(); }

        void Refresh2() { Batch(Refresh2Core); }


        void Refresh2Core()
        {
            var r = State.Ramp;
            var a = State.Result;
            bool rebuild = builtMin != r.Min || builtMax != r.Max || ramp.Rows.Count != r.Rows;
            if (rebuild)
            {
                ramp.Rows.Clear();
                for (int i = 0; i < r.Rows; i++) ramp.Rows.Add("", "", "", "", "", "");
                builtMin = r.Min; builtMax = r.Max;
                GridUtil.FitHeight(ramp, 46);
            }
            for (int i = 0; i < r.Rows; i++)
            {
                var row = ramp.Rows[i];
                int v = r.Min + i;
                row.Cells[0].Value = Theme.CountLabel(v, r.Min, r.Max);
                bool editing = ramp.IsCurrentCellInEditMode && ramp.CurrentCell != null && ramp.CurrentCell.RowIndex == i;
                if (!editing || ramp.CurrentCell.ColumnIndex != 3) row.Cells[3].Value = r.Units[i].ToString("0.##", CultureInfo.InvariantCulture);
                if (!editing || ramp.CurrentCell.ColumnIndex != 4) row.Cells[4].Value = r.Hands[i].ToString(CultureInfo.InvariantCulture);
                double units = r.Units[i];
                row.Cells[5].Value = units <= 0 ? "sit out" : Theme.Money(units * State.Unit) + (r.Hands[i] > 1 ? " × " + r.Hands[i] : "");
                row.Cells[5].Style.ForeColor = units <= 0 ? Theme.Muted : Theme.Text;
                if (a != null && a.RowFreq != null && i < a.RowFreq.Length)
                {
                    row.Cells[1].Value = Theme.Pct(a.RowFreq[i], 2);
                    row.Cells[2].Value = a.RowN[i] > 0 ? Theme.SignedPct(a.RowEV[i], 2) : "—";
                    row.Cells[2].Style.ForeColor = a.RowEV[i] >= 0 ? Theme.Good : Theme.Bad;
                }
                else { row.Cells[1].Value = "—"; row.Cells[2].Value = "—"; }
            }

            if (State.Sim == null) banner.Show("No simulation yet — press Run simulation (F5).", false);
            else if (State.SimStale) banner.Show("⚠  Settings changed since the last simulation; these results are for the previous settings. Press F5 to re-run.", true);
            else if (a != null && a.UsedAssumedCov) banner.Show("Multi-hand rows use an assumed correlation of 0.37 because the simulation was heads-up. Use 2+ spots for measured covariance.", false);
            else if (State.SimRunning) banner.Show("Simulation running — results refine as more rounds come in.", false);
            else banner.Visible = false;

            if (a == null || !a.Valid)
            {
                foreach (var t in new[] { tWin, tSd, tEdge, tScore, tN0, tRor, tBank, tKelly, tCe, tSpread, tDouble, tHouse }) t.Set("—", "");
                contrib.Rows.Clear();
                return;
            }
            tWin.Set(Theme.Money(a.WinPerHour), Theme.Money(a.WinPer100) + " per 100 rounds  ·  ± " + Theme.Money(a.SeWinPerHour) + " sim error", a.WinPerHour >= 0 ? Theme.Good : Theme.Bad);
            tSd.Set(Theme.Money0(a.SdPerHour), Theme.Money0(a.SdPer100) + " per 100 rounds");
            tEdge.Set(Theme.SignedPct(a.EdgePct / 100, 3), "avg bet " + Theme.Money(a.AvgBetPlayed * State.Unit) + " when playing", a.EdgePct >= 0 ? Theme.Good : Theme.Bad);
            tScore.Set(Theme.F(a.Score, "0.00"), "win/100 rds on a $10k Kelly bankroll  ·  DI " + Theme.F(a.DI, "0.00"));
            tN0.Set(a.EV > 0 ? Theme.F(a.N0, "#,0") + " rds" : "never", a.EV > 0 ? "≈ " + Theme.F(a.HoursN0, "#,0") + " hours" : "negative expectation");
            tRor.Set(Theme.Pct(a.ROR, a.ROR < 0.001 ? 4 : 2), "lifetime, " + Theme.Money0(State.Bankroll) + " bankroll", a.ROR > 0.2 ? Theme.Bad : (a.ROR < 0.05 ? Theme.Good : Theme.Text));
            tBank.Set(Theme.Money0(a.BankrollForRor(0.05)), "13.5% ROR: " + Theme.Money0(a.BankrollForRor(0.135)) + "  ·  1%: " + Theme.Money0(a.BankrollForRor(0.01)));
            tKelly.Set(Theme.Money(a.KellyUnit), a.KellyUnit > 0 ? "your unit is " + Theme.F(State.Unit / a.KellyUnit, "0.00") + "× Kelly" : "");
            tCe.Set(Theme.Money(a.CEPerHour) + "/h", "risk-adjusted win for your bankroll", a.CEPerHour >= 0 ? Theme.Good : Theme.Bad);
            tSpread.Set("1–" + Theme.F(a.Spread, "0.#"), "you play " + Theme.Pct(a.PlayedFrac, 1) + " of rounds");
            tDouble.Set(a.WinPerHour > 0 ? Theme.F(State.Bankroll / a.WinPerHour, "#,0") + " h" : "—", "expected, at this unit");
            double he = State.Sim.BasicEV;
            tHouse.Set(Theme.SignedPct(he, 3), "player EV off the top (CA)", he >= 0 ? Theme.Good : Theme.Bad);

            contrib.Rows.Clear();
            double totalWin = 0, totalVar = 0;
            var bins = Analysis.Rows(Analysis.Bins(State.Sim), r);
            var varShare = new double[r.Rows];
            for (int i = 0; i < r.Rows; i++)
            {
                totalWin += a.RowContribution[i];
                double u = r.Units[i]; int h = Math.Max(1, r.Hands[i]);
                if (u > 0) varShare[i] = bins[i].Freq * u * u * (h * bins[i].M2 + h * (h - 1) * bins[i].Cov);
                totalVar += varShare[i];
            }
            for (int i = 0; i < r.Rows; i++)
            {
                if (a.RowFreq[i] <= 0) continue;
                double u = r.Units[i];
                int idx = contrib.Rows.Add(Theme.CountLabel(r.Min + i, r.Min, r.Max), Theme.Pct(a.RowFreq[i], 2), Theme.SignedPct(a.RowEV[i], 2),
                    u <= 0 ? "sit out" : Theme.Money(u * State.Unit * Math.Max(1, r.Hands[i])),
                    Theme.Money(a.RowContribution[i] * 100 * State.Unit),
                    totalWin != 0 ? Theme.Pct(a.RowContribution[i] / totalWin, 1) : "—",
                    totalVar > 0 ? Theme.Pct(varShare[i] / totalVar, 1) : "—");
                contrib.Rows[idx].Cells[4].Style.ForeColor = a.RowContribution[i] >= 0 ? Theme.Good : Theme.Bad;
            }
            GridUtil.FitHeight(contrib, 46);
        }
    }
}
