using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.Text;
using System.Windows.Forms;
using CalcBJ.Engine;

namespace CalcBJ.App
{
    internal sealed class ComparePage : PageBase
    {
        readonly TextBox name;
        readonly DataGridView grid;

        delegate string Getter(CompareEntry e);

        sealed class Metric
        {
            public string Label;
            public Getter Get;
            public Func<CompareEntry, double> Value;   // for highlighting the best column; null = no highlight
            public bool HigherIsBetter;
            public Metric(string l, Getter g, Func<CompareEntry, double> v, bool hib) { Label = l; Get = g; Value = v; HigherIsBetter = hib; }
        }

        static readonly List<Metric> Metrics = new List<Metric>
        {
            new Metric("Rules", e => e.Rules, null, true),
            new Metric("Count", e => e.Count, null, true),
            new Metric("Playing strategy", e => e.Strategy, null, true),
            new Metric("Bet ramp (units)", e => e.Ramp, null, true),
            new Metric("Spots / rounds per hour", e => e.Spots + " / " + e.RoundsPerHour.ToString("0", CultureInfo.InvariantCulture), null, true),
            new Metric("Unit / bankroll", e => Theme.Money(e.Unit) + " / " + Theme.Money0(e.Bankroll), null, true),
            new Metric("Basic strategy house edge", e => Theme.Pct(e.HouseEdge, 3), e => -e.HouseEdge, true),
            new Metric("Win rate per hour", e => Theme.Money(e.WinPerHour), e => e.WinPerHour, true),
            new Metric("SD per hour", e => Theme.Money0(e.SdPerHour), null, true),
            new Metric("Win per 100 rounds", e => Theme.Money(e.WinPer100), e => e.WinPer100, true),
            new Metric("Player edge", e => Theme.SignedPct(e.EdgePct / 100, 3), e => e.EdgePct, true),
            new Metric("Average bet", e => Theme.Money(e.AvgBet), null, true),
            new Metric("Spread / rounds played", e => "1–" + e.Spread.ToString("0.#", CultureInfo.InvariantCulture) + " / " + e.PlayedPct.ToString("0.#", CultureInfo.InvariantCulture) + "%", null, true),
            new Metric("SCORE", e => e.Score.ToString("0.00", CultureInfo.InvariantCulture), e => e.Score, true),
            new Metric("DI", e => e.DI.ToString("0.00", CultureInfo.InvariantCulture), e => e.DI, true),
            new Metric("N0 (rounds / hours)", e => Theme.F(e.N0, "#,0") + " / " + Theme.F(e.HoursN0, "#,0"), e => -e.N0, true),
            new Metric("Risk of ruin", e => Theme.Pct(e.ROR, 2), e => -e.ROR, true),
            new Metric("Bankroll for 5% ROR", e => Theme.Money0(e.Bankroll5), e => -e.Bankroll5, true),
            new Metric("Kelly-optimal unit", e => Theme.Money(e.KellyUnit), null, true),
            new Metric("Certainty equivalent / hour", e => Theme.Money(e.CEPerHour), e => e.CEPerHour, true),
            new Metric("Rounds simulated", e => Theme.Count(e.Rounds) + (e.Stale ? " (stale)" : ""), null, true),
        };

        public ComparePage(AppState s)
            : base(s, "Compare games", "Snapshot the current game, ramp and results, then line several up side by side.", 100)
        {
            var col = Column(0);
            var c = Stack(col, new Card("Scenarios"));
            var strip = c.AddRow(new Strip(true));
            strip.Add(Theme.Lbl("Name"));
            name = strip.Add(new TextBox());
            name.Width = Theme.S(260);
            name.Font = Theme.Body;
            var add = strip.Add(Theme.Primary("Add current scenario"));
            var del = strip.Add(Theme.Secondary("Remove selected column"));
            var clr = strip.Add(Theme.Secondary("Clear all"));
            var copy = strip.Add(Theme.Secondary("Copy table"));
            c.AddRow(Theme.Wrap("Best value in each row is highlighted. Scenarios are saved inside the .bjev file.", 900));
            grid = c.AddRow(new DataGridView());
            Theme.StyleGrid(grid);
            grid.ReadOnly = true;
            grid.ScrollBars = ScrollBars.Both;
            grid.DefaultCellStyle.WrapMode = DataGridViewTriState.True;
            grid.AutoSizeRowsMode = DataGridViewAutoSizeRowsMode.AllCells;
            grid.Dock = DockStyle.Fill;
            grid.Height = Theme.S(640);

            add.Click += (o, e) =>
            {
                if (State.Sim == null) { MessageBox.Show(FindForm(), "Run a simulation first.", "CalcBJ"); return; }
                string n = name.Text.Trim();
                if (n.Length == 0) n = "Game " + (State.Compare.Count + 1);
                State.Compare.Add(State.Snapshot(n));
                name.Text = "";
                State.NotifyCompare();
            };
            del.Click += (o, e) =>
            {
                if (grid.CurrentCell == null || grid.CurrentCell.ColumnIndex < 1) return;
                int i = grid.CurrentCell.ColumnIndex - 1;
                if (i < State.Compare.Count) { State.Compare.RemoveAt(i); State.NotifyCompare(); }
            };
            clr.Click += (o, e) => { State.Compare.Clear(); State.NotifyCompare(); };
            copy.Click += (o, e) => CopyTable();
            State.CompareChanged += (o, e) => Rebuild();
            State.Reloaded += (o, e) => Rebuild();
            Rebuild();
        }

        void Rebuild() { Batch(RebuildCore); }


        void RebuildCore()
        {
            grid.Columns.Clear();
            var mc = Theme.TextCol("", 200, false, true);
            mc.DefaultCellStyle.Font = Theme.BodyBold;
            mc.DefaultCellStyle.ForeColor = Theme.Muted;
            mc.Frozen = true;
            grid.Columns.Add(mc);
            foreach (var e in State.Compare)
            {
                var col = Theme.TextCol(e.Name, 230, false, true);
                grid.Columns.Add(col);
            }
            foreach (var m in Metrics)
            {
                var row = new List<object>();
                row.Add(m.Label);
                foreach (var e in State.Compare) row.Add(m.Get(e));
                int ri = grid.Rows.Add(row.ToArray());
                if (m.Value != null && State.Compare.Count > 1)
                {
                    int best = -1; double bv = double.NegativeInfinity;
                    for (int i = 0; i < State.Compare.Count; i++)
                    {
                        double v = m.Value(State.Compare[i]);
                        if (!double.IsNaN(v) && v > bv) { bv = v; best = i; }
                    }
                    if (best >= 0)
                    {
                        var cell = grid.Rows[ri].Cells[best + 1];
                        cell.Style.BackColor = Theme.AccentSoft;
                        cell.Style.Font = Theme.BodyBold;
                        cell.Style.ForeColor = Theme.AccentDark;
                    }
                }
            }
            if (State.Compare.Count == 0)
            {
                grid.Columns.Add(Theme.TextCol("No scenarios yet", 300, false, true));
                foreach (DataGridViewRow r in grid.Rows) r.Cells[1].Value = "";
                grid.Rows[0].Cells[1].Value = "Press “Add current scenario”.";
            }
        }

        void CopyTable()
        {
            var sb = new StringBuilder();
            sb.Append("Metric");
            foreach (var e in State.Compare) sb.Append('\t').Append(e.Name);
            sb.AppendLine();
            foreach (var m in Metrics)
            {
                sb.Append(m.Label);
                foreach (var e in State.Compare) sb.Append('\t').Append(m.Get(e));
                sb.AppendLine();
            }
            try { Clipboard.SetText(sb.ToString()); } catch { }
        }
    }
}
