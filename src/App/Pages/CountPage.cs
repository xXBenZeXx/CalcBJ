using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.Windows.Forms;
using CalcBJ.Engine;

namespace CalcBJ.App
{
    internal sealed class CountPage : PageBase
    {
        readonly ComboBox preset, metric, deckEst, rounding, indexMode, viewAt;
        readonly DataGridView tags, overrides, indexList;
        readonly CheckBox redTags, showIdx;
        readonly NumericUpDown ircBase, ircPerDeck, idxMin, idxMax;
        readonly Label countInfo, ircInfo, metricNote, modeNote;
        readonly StrategyChart hard, soft, pairs;
        readonly Timer refresh = new Timer();
        readonly List<CountSystem> presets = CountSystem.Presets();
        static readonly string[] PlayNames = { "Stand", "Hit", "Double", "No double", "Split", "No split", "Surrender", "No surrender", "Insure" };
        static readonly string[] UpNames = { "2", "3", "4", "5", "6", "7", "8", "9", "T", "A" };
        bool loading;
        double[] eorCache;
        string eorKey;

        public CountPage(AppState s)
            : base(s, "Count & strategy", "Counting system, true-count conversion and the index plays used in the simulation.", 50, 50)
        {
            var left = Column(0);
            var right = Column(1);

            // ---- count system
            var cc = Stack(left, new Card("Counting system"));
            var g = cc.AddRow(new FieldGrid());
            var names = new List<string>();
            foreach (var p in presets) names.Add(p.Name);
            names.Add("Custom");
            preset = g.Add("System", Theme.Combo(220, names.ToArray()));
            tags = cc.AddRow(new DataGridView());
            Theme.StyleGrid(tags);
            tags.ScrollBars = ScrollBars.None;
            tags.SelectionMode = DataGridViewSelectionMode.CellSelect;
            var lab = Theme.TextCol("Card", 72, false, true);
            lab.DefaultCellStyle.Font = Theme.BodyBold;
            lab.DefaultCellStyle.BackColor = Theme.GridHeader;
            tags.Columns.Add(lab);
            string[] ranks = { "A", "2", "3", "4", "5", "6", "7", "8", "9", "T" };
            foreach (string r in ranks)
            {
                var c = Theme.TextCol(r, 40, false, false);
                c.DefaultCellStyle.Alignment = DataGridViewContentAlignment.MiddleCenter;
                c.HeaderCell.Style.Alignment = DataGridViewContentAlignment.MiddleCenter;
                tags.Columns.Add(c);
            }
            tags.Rows.Add("Tag");
            tags.Rows.Add("Red cards");
            tags.Width = Theme.S(72 + 40 * 10) + 2;
            tags.Dock = DockStyle.None;
            tags.Height = tags.ColumnHeadersHeight + 2 * tags.RowTemplate.Height + 2;
            tags.CellEndEdit += (o, e) => SaveTags();
            redTags = cc.AddRow(Theme.Check("Separate tags for red cards (e.g. Red Seven)"));
            countInfo = cc.AddRow(Theme.Wrap("", 560));
            var ig = cc.AddRow(new FieldGrid());
            var ircStrip = ig.Add("Initial running count", new Strip());
            ircBase = ircStrip.Add(Theme.Num(-200, 200, 0, 1, 1, 70));
            ircStrip.Add(Theme.Lbl("+"));
            ircPerDeck = ircStrip.Add(Theme.Num(-20, 20, 0, 1, 1, 70));
            ircStrip.Add(Theme.Lbl("× decks"));
            ircInfo = cc.AddRow(Theme.MutedLbl(""));

            // ---- conversion
            var tc = Stack(left, new Card("True count conversion"));
            g = tc.AddRow(new FieldGrid());
            metric = g.Add("Bet and play by", Theme.Combo(220, "True count", "Running count"));
            deckEst = g.Add("Deck estimation", Theme.Combo(220, "Exact (cards remaining)", "Nearest quarter deck", "Nearest half deck", "Nearest full deck"));
            rounding = g.Add("True count rounding", Theme.Combo(220, "Floor (round down)", "Truncate (toward zero)", "Round to nearest"));
            metricNote = tc.AddRow(Theme.Wrap("", 560));

            // ---- strategy
            var st = Stack(left, new Card("Playing strategy"));
            g = st.AddRow(new FieldGrid());
            indexMode = g.Add("Index plays", Theme.Combo(260, "Basic strategy only", "Illustrious 18", "Illustrious 18 + Fab 4", "Full index set"));
            var rangeStrip = g.Add("Index range", new Strip());
            idxMin = rangeStrip.Add(Theme.Num(-100, 100, -10, 0, 1, 64));
            rangeStrip.Add(Theme.Lbl("to"));
            idxMax = rangeStrip.Add(Theme.Num(-100, 100, 10, 0, 1, 64));
            modeNote = st.AddRow(Theme.Wrap("Indices are generated for the selected count and rules by combinatorial analysis of the "
                + "average shoe composition at each count, so any tag set gets its own index numbers.", 560));

            // ---- overrides
            var ov = Stack(left, new Card("Custom index plays"));
            ov.AddRow(Theme.Wrap("Rows here replace the computed decision for that play everywhere: e.g. Hard 16 v T, Stand, ≥, 0 "
                + "means stand at 0 or more and hit below.", 560));
            overrides = ov.AddRow(new DataGridView());
            Theme.StyleGrid(overrides);
            overrides.EditMode = DataGridViewEditMode.EditOnEnter;
            var on = new DataGridViewCheckBoxColumn(); on.HeaderText = "On"; on.Width = Theme.S(36);
            overrides.Columns.Add(on);
            overrides.Columns.Add(ComboCol("Hand", 80, "Hard", "Soft", "Pair", "Insurance"));
            var tot = Theme.TextCol("Total", 52, true, false); overrides.Columns.Add(tot);
            overrides.Columns.Add(ComboCol("Dealer", 58, UpNames));
            overrides.Columns.Add(ComboCol("Play", 108, PlayNames));
            overrides.Columns.Add(ComboCol("When", 52, "≥", "≤"));
            overrides.Columns.Add(Theme.TextCol("Index", 58, true, false));
            overrides.Dock = DockStyle.Fill;
            overrides.Height = Theme.S(180);
            overrides.CurrentCellDirtyStateChanged += (o, e) =>
            {
                if (overrides.IsCurrentCellDirty && !(overrides.CurrentCell is DataGridViewTextBoxCell)) overrides.CommitEdit(DataGridViewDataErrorContexts.Commit);
            };
            overrides.CellValueChanged += (o, e) => { if (!loading) SaveOverrides(); };
            overrides.DataError += (o, e) => { e.ThrowException = false; };
            var ob = ov.AddRow(new Strip(true));
            var add = ob.Add(Theme.Secondary("Add row"));
            add.Click += (o, e) => { AddOverrideRow(new IndexOverride()); SaveOverrides(); };
            var del = ob.Add(Theme.Secondary("Remove row"));
            del.Click += (o, e) =>
            {
                if (overrides.CurrentRow != null) { overrides.Rows.Remove(overrides.CurrentRow); SaveOverrides(); }
            };
            var pub = ob.Add(Theme.Secondary("Add published Hi-Lo I18 + Fab 4"));
            pub.Click += (o, e) => { LoadPublished(); };
            var clr = ob.Add(Theme.Secondary("Clear"));
            clr.Click += (o, e) => { overrides.Rows.Clear(); SaveOverrides(); };

            // ---- chart
            var ch = Stack(right, new Card("Strategy chart"));
            var vs = ch.AddRow(new FieldGrid());
            viewAt = vs.Add("Show strategy at", Theme.Combo(220));
            showIdx = vs.AddFull(Theme.Check("Mark index plays in the chart"));
            showIdx.Checked = true;
            var legend = ch.AddRow(new Strip(true));
            AddLegend(legend, "H", "Hit"); AddLegend(legend, "S", "Stand"); AddLegend(legend, "D", "Double");
            AddLegend(legend, "P", "Split"); AddLegend(legend, "R", "Surrender");
            var mk = legend.Add(Theme.MutedLbl("▲ = index play (S≥+1: stand at +1 or more)"));
            mk.ForeColor = Theme.IndexMark;
            hard = ch.AddRow(new StrategyChart(StrategyChart.ChartKind.Hard));
            soft = ch.AddRow(new StrategyChart(StrategyChart.ChartKind.Soft));
            pairs = ch.AddRow(new StrategyChart(StrategyChart.ChartKind.Pair));
            hard.Margin = new Padding(0, 0, 0, Theme.S(8));
            soft.Margin = new Padding(0, 0, 0, Theme.S(8));

            var il = Stack(right, new Card("Index plays in use"));
            il.AddRow(Theme.Wrap("Every decision that differs from basic strategy somewhere in the index range, for the selected count.", 560));
            indexList = il.AddRow(new DataGridView());
            Theme.StyleGrid(indexList);
            indexList.ReadOnly = true;
            indexList.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
            indexList.Columns.Add(Theme.TextCol("Hand", 110, false, true));
            indexList.Columns.Add(Theme.TextCol("Play", 120, false, true));
            indexList.Columns.Add(Theme.TextCol("When", 160, false, true));
            indexList.Columns[2].AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill;
            indexList.Dock = DockStyle.Fill;
            indexList.Height = Theme.S(300);
            GridUtil.NoSelection(indexList);

            refresh.Interval = 250;
            refresh.Tick += (o, e) => { refresh.Stop(); RefreshStrategy(); };

            LoadFromState();
            HookEvents();
            State.Reloaded += (o, e) => { LoadFromState(); RefreshStrategy(); };
            State.ConfigChanged += (o, e) => { if (!loading) { RefreshInfo(); refresh.Stop(); refresh.Start(); } };
        }

        static DataGridViewComboBoxColumn ComboCol(string header, int width, params string[] items)
        {
            var c = new DataGridViewComboBoxColumn();
            c.HeaderText = header;
            c.Width = Theme.S(width);
            c.Items.AddRange(items);
            c.FlatStyle = FlatStyle.Flat;
            c.DisplayStyle = DataGridViewComboBoxDisplayStyle.Nothing;
            c.SortMode = DataGridViewColumnSortMode.NotSortable;
            return c;
        }

        static void AddLegend(Strip s, string code, string text)
        {
            var l = new Label();
            l.Text = code;
            l.AutoSize = false;
            l.Size = new Size(Theme.S(24), Theme.S(20));
            l.TextAlign = ContentAlignment.MiddleCenter;
            l.BackColor = Theme.ActionColor(code);
            l.Font = Theme.BodyBold;
            l.Margin = new Padding(0, Theme.S(4), Theme.S(4), Theme.S(4));
            s.Controls.Add(l);
            var t = Theme.MutedLbl(text);
            t.Margin = new Padding(0, Theme.S(7), Theme.S(12), Theme.S(4));
            s.Controls.Add(t);
        }

        void HookEvents()
        {
            preset.SelectedIndexChanged += (o, e) => { if (!loading) ApplyPreset(); };
            redTags.CheckedChanged += (o, e) => { if (!loading) { SaveTags(); } };
            EventHandler conv = (o, e) => { if (!loading) SaveConversion(); };
            ircBase.ValueChanged += conv; ircPerDeck.ValueChanged += conv;
            metric.SelectedIndexChanged += conv; deckEst.SelectedIndexChanged += conv; rounding.SelectedIndexChanged += conv;
            EventHandler strat = (o, e) => { if (!loading) SaveStrategy(); };
            indexMode.SelectedIndexChanged += strat; idxMin.ValueChanged += strat; idxMax.ValueChanged += strat;
            viewAt.SelectedIndexChanged += (o, e) => { if (!loading) RenderCharts(); };
            showIdx.CheckedChanged += (o, e) => RenderCharts();
        }

        void LoadFromState() { Batch(LoadFromStateCore); }


        void LoadFromStateCore()
        {
            loading = true;
            var cs = State.Count;
            int pi = presets.FindIndex(p => p.Name == cs.Name);
            preset.SelectedIndex = pi >= 0 ? pi : presets.Count;
            redTags.Checked = cs.RedTags != null;
            for (int i = 0; i < 10; i++)
            {
                tags.Rows[0].Cells[i + 1].Value = Fmt(cs.Tags[i]);
                tags.Rows[1].Cells[i + 1].Value = Fmt(cs.RedTags != null ? cs.RedTags[i] : cs.Tags[i]);
            }
            tags.Rows[0].Cells[0].Value = cs.RedTags != null ? "Black" : "Tag";
            tags.Rows[1].Visible = cs.RedTags != null;
            tags.Height = tags.ColumnHeadersHeight + (cs.RedTags != null ? 2 : 1) * tags.RowTemplate.Height + 2;
            ircBase.Value = (decimal)Math.Max(-200, Math.Min(200, cs.IrcBase));
            ircPerDeck.Value = (decimal)Math.Max(-20, Math.Min(20, cs.IrcPerDeck));
            metric.SelectedIndex = (int)cs.Metric;
            deckEst.SelectedIndex = (int)cs.DeckEst;
            rounding.SelectedIndex = (int)cs.Rounding;
            indexMode.SelectedIndex = (int)State.Strategy.Mode;
            idxMin.Value = Math.Max(idxMin.Minimum, Math.Min(idxMin.Maximum, State.Strategy.IndexMin));
            idxMax.Value = Math.Max(idxMax.Minimum, Math.Min(idxMax.Maximum, State.Strategy.IndexMax));
            overrides.Rows.Clear();
            foreach (var o in State.Strategy.Overrides) AddOverrideRow(o);
            loading = false;
            RefreshInfo();
        }

        static string Fmt(double v) { return v.ToString("+0.##;-0.##;0", CultureInfo.InvariantCulture); }

        void ApplyPreset()
        {
            if (preset.SelectedIndex < 0 || preset.SelectedIndex >= presets.Count) return;
            var p = presets[preset.SelectedIndex].Clone();
            p.DeckEst = State.Count.DeckEst;
            p.Rounding = State.Count.Rounding;
            State.Count = p;
            AfterCountChange();
        }

        void AfterCountChange()
        {
            State.ResetRampRange();
            State.ResetIndexRange();
            State.ApplyPreset(State.RampPreset);
            LoadFromState();
            State.NotifyConfig();
            State.NotifyBetting();
        }

        void SaveTags()
        {
            if (loading) return;
            var cs = State.Count.Clone();
            for (int i = 0; i < 10; i++)
            {
                double v;
                if (Theme.TryParse(Convert.ToString(tags.Rows[0].Cells[i + 1].Value, CultureInfo.InvariantCulture), out v)) cs.Tags[i] = Math.Max(-20, Math.Min(20, v));
            }
            if (redTags.Checked)
            {
                if (cs.RedTags == null) cs.RedTags = (double[])cs.Tags.Clone();
                for (int i = 0; i < 10; i++)
                {
                    double v;
                    if (tags.Rows[1].Visible && Theme.TryParse(Convert.ToString(tags.Rows[1].Cells[i + 1].Value, CultureInfo.InvariantCulture), out v)) cs.RedTags[i] = Math.Max(-20, Math.Min(20, v));
                }
            }
            else cs.RedTags = null;
            bool same = cs.Key() == State.Count.Key();
            int pi = presets.FindIndex(p => p.Key() == cs.Key());
            cs.Name = pi >= 0 ? presets[pi].Name : (same ? State.Count.Name : "Custom");
            State.Count = cs;
            if (!same) AfterCountChange(); else LoadFromState();
        }

        void SaveConversion()
        {
            var cs = State.Count;
            cs.IrcBase = (double)ircBase.Value;
            cs.IrcPerDeck = (double)ircPerDeck.Value;
            var oldMetric = cs.Metric;
            cs.Metric = (CountMetric)metric.SelectedIndex;
            cs.DeckEst = (DeckEstimation)deckEst.SelectedIndex;
            cs.Rounding = (TcRounding)rounding.SelectedIndex;
            if (cs.Metric != oldMetric || cs.Metric == CountMetric.RunningCount || !cs.IsBalanced)
            {
                State.ResetRampRange();
                State.ResetIndexRange();
                State.ApplyPreset(State.RampPreset);
                loading = true;
                idxMin.Value = Math.Max(idxMin.Minimum, Math.Min(idxMin.Maximum, State.Strategy.IndexMin));
                idxMax.Value = Math.Max(idxMax.Minimum, Math.Min(idxMax.Maximum, State.Strategy.IndexMax));
                loading = false;
            }
            RefreshInfo();
            State.NotifyConfig();
            State.NotifyBetting();
        }

        void SaveStrategy()
        {
            State.Strategy.Mode = (IndexMode)indexMode.SelectedIndex;
            State.Strategy.IndexMin = (int)idxMin.Value;
            State.Strategy.IndexMax = (int)idxMax.Value;
            State.NotifyConfig();
        }

        void AddOverrideRow(IndexOverride o)
        {
            bool old = loading;
            loading = true;
            string total;
            if (o.Kind == HandKind.Pair) total = Cards.RankName(o.Total);
            else if (o.Kind == HandKind.Insurance) total = "";
            else total = o.Total.ToString(CultureInfo.InvariantCulture);
            string up = o.Upcard == 1 ? "A" : (o.Upcard == 10 ? "T" : o.Upcard.ToString(CultureInfo.InvariantCulture));
            overrides.Rows.Add(o.Enabled, o.Kind.ToString(), total, up, PlayNames[Math.Max(0, Math.Min(PlayNames.Length - 1, PlayIndex(o.Play)))],
                o.AtOrAbove ? "≥" : "≤", o.Index.ToString("+0.##;-0.##;0", CultureInfo.InvariantCulture));
            GridUtil.FitHeight(overrides, 12);
            overrides.Height = Math.Max(overrides.Height, Theme.S(120));
            loading = old;
        }

        static int PlayIndex(PlayKind p)
        {
            switch (p)
            {
                case PlayKind.Stand: return 0;
                case PlayKind.Hit: return 1;
                case PlayKind.Double: return 2;
                case PlayKind.NoDouble: return 3;
                case PlayKind.Split: return 4;
                case PlayKind.NoSplit: return 5;
                case PlayKind.Surrender: return 6;
                case PlayKind.NoSurrender: return 7;
                default: return 8;
            }
        }

        static readonly PlayKind[] PlayKinds = { PlayKind.Stand, PlayKind.Hit, PlayKind.Double, PlayKind.NoDouble, PlayKind.Split, PlayKind.NoSplit, PlayKind.Surrender, PlayKind.NoSurrender, PlayKind.Insure };

        static int ParseRank(string s)
        {
            s = (s ?? "").Trim().ToUpperInvariant();
            if (s == "A" || s == "1" || s == "11") return 1;
            if (s == "T" || s == "10" || s == "J" || s == "Q" || s == "K") return 10;
            int v;
            if (int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out v) && v >= 2 && v <= 9) return v;
            return -1;
        }

        void SaveOverrides()
        {
            var list = new List<IndexOverride>();
            foreach (DataGridViewRow row in overrides.Rows)
            {
                var o = new IndexOverride();
                o.Enabled = row.Cells[0].Value is bool && (bool)row.Cells[0].Value;
                string kind = Convert.ToString(row.Cells[1].Value);
                o.Kind = kind == "Soft" ? HandKind.Soft : (kind == "Pair" ? HandKind.Pair : (kind == "Insurance" ? HandKind.Insurance : HandKind.Hard));
                string total = Convert.ToString(row.Cells[2].Value, CultureInfo.InvariantCulture) ?? "";
                if (o.Kind == HandKind.Pair) o.Total = ParseRank(total.Split(',')[0]);
                else if (o.Kind == HandKind.Soft)
                {
                    string t = total.Trim().ToUpperInvariant().Replace("A", "").Replace(",", "");
                    int v;
                    if (int.TryParse(t, NumberStyles.Integer, CultureInfo.InvariantCulture, out v)) o.Total = v <= 10 ? v + 11 : v;
                }
                else if (o.Kind == HandKind.Hard)
                {
                    int v;
                    if (int.TryParse(total.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out v)) o.Total = v;
                }
                o.Upcard = ParseRank(Convert.ToString(row.Cells[3].Value));
                int pi = Array.IndexOf(PlayNames, Convert.ToString(row.Cells[4].Value));
                o.Play = pi >= 0 ? PlayKinds[pi] : PlayKind.Stand;
                if (o.Kind == HandKind.Insurance) { o.Play = PlayKind.Insure; o.Upcard = 1; }
                o.AtOrAbove = Convert.ToString(row.Cells[5].Value) != "≤";
                double idx;
                o.Index = Theme.TryParse(Convert.ToString(row.Cells[6].Value, CultureInfo.InvariantCulture), out idx) ? idx : 0;
                bool valid = o.Kind == HandKind.Insurance || (o.Upcard >= 1 && ((o.Kind == HandKind.Pair && o.Total >= 1) ||
                    (o.Kind == HandKind.Soft && o.Total >= 12 && o.Total <= 21) || (o.Kind == HandKind.Hard && o.Total >= 4 && o.Total <= 21)));
                row.DefaultCellStyle.ForeColor = valid ? Theme.Text : Theme.Bad;
                if (valid) list.Add(o);
            }
            State.Strategy.Overrides = list;
            State.NotifyConfig();
        }

        void LoadPublished()
        {
            // Published Hi-Lo multi-deck S17 values (Illustrious 18 and Fab 4).
            object[,] rows =
            {
                { HandKind.Insurance, 0, 1, PlayKind.Insure, 3 },
                { HandKind.Hard, 16, 10, PlayKind.Stand, 0 }, { HandKind.Hard, 15, 10, PlayKind.Stand, 4 },
                { HandKind.Pair, 10, 5, PlayKind.Split, 5 }, { HandKind.Pair, 10, 6, PlayKind.Split, 4 },
                { HandKind.Hard, 10, 10, PlayKind.Double, 4 }, { HandKind.Hard, 12, 3, PlayKind.Stand, 2 },
                { HandKind.Hard, 12, 2, PlayKind.Stand, 3 }, { HandKind.Hard, 11, 1, PlayKind.Double, 1 },
                { HandKind.Hard, 9, 2, PlayKind.Double, 1 }, { HandKind.Hard, 10, 1, PlayKind.Double, 4 },
                { HandKind.Hard, 9, 7, PlayKind.Double, 3 }, { HandKind.Hard, 16, 9, PlayKind.Stand, 5 },
                { HandKind.Hard, 13, 2, PlayKind.Stand, -1 }, { HandKind.Hard, 12, 4, PlayKind.Stand, 0 },
                { HandKind.Hard, 12, 5, PlayKind.Stand, -2 }, { HandKind.Hard, 12, 6, PlayKind.Stand, -1 },
                { HandKind.Hard, 13, 3, PlayKind.Stand, -2 },
                { HandKind.Hard, 14, 10, PlayKind.Surrender, 3 }, { HandKind.Hard, 15, 10, PlayKind.Surrender, 0 },
                { HandKind.Hard, 15, 9, PlayKind.Surrender, 2 }, { HandKind.Hard, 15, 1, PlayKind.Surrender, 1 },
            };
            for (int i = 0; i < rows.GetLength(0); i++)
            {
                var o = new IndexOverride();
                o.Kind = (HandKind)rows[i, 0]; o.Total = (int)rows[i, 1]; o.Upcard = (int)rows[i, 2];
                o.Play = (PlayKind)rows[i, 3]; o.Index = (int)rows[i, 4]; o.AtOrAbove = true; o.Enabled = true;
                AddOverrideRow(o);
            }
            SaveOverrides();
        }

        void RefreshInfo() { Batch(RefreshInfoCore); }


        void RefreshInfoCore()
        {
            var cs = State.Count;
            var r = State.Rules;
            double imb = cs.ImbalancePerDeck();
            string bal = cs.IsBalanced ? "Balanced" : "Unbalanced (" + Fmt(imb) + " per deck)";
            double bc, ic;
            Correlations(out bc, out ic);
            countInfo.Text = string.Format(CultureInfo.InvariantCulture, "{0}  ·  level {1}  ·  betting correlation {2:0.000}  ·  insurance correlation {3:0.000}  ·  1 Hi-Lo TC ≈ {4:0.##} of this count",
                bal, cs.Level().ToString("0.##", CultureInfo.InvariantCulture), bc, ic, CountScale.Factor(cs));
            ircInfo.Text = string.Format(CultureInfo.InvariantCulture, "Running count starts at {0} for {1} deck(s).", Fmt(cs.Irc(r.Decks)), r.Decks);
            if (cs.Metric == CountMetric.RunningCount)
                metricNote.Text = "Bets and index plays key off the running count itself; true-count settings are ignored. "
                    + "Typical for unbalanced counts such as KO and Red Seven.";
            else if (!cs.IsBalanced)
                metricNote.Text = "This count is unbalanced: the true count is the running count (including the IRC) divided by decks remaining. Set the IRC to suit.";
            else
                metricNote.Text = "True count = running count ÷ estimated decks remaining (unseen cards, " + DeckText(cs.DeckEst) + "), then " + cs.Rounding.ToString().ToLowerInvariant() + "ed to a whole number.";
            deckEst.Enabled = rounding.Enabled = cs.Metric == CountMetric.TrueCount;
        }

        static string DeckText(DeckEstimation d)
        {
            switch (d)
            {
                case DeckEstimation.Exact: return "exact";
                case DeckEstimation.QuarterDeck: return "to the nearest ¼ deck";
                case DeckEstimation.HalfDeck: return "to the nearest ½ deck";
                default: return "to the nearest deck";
            }
        }

        void Correlations(out double bc, out double ic)
        {
            var r = State.Rules;
            string key = r.Key();
            if (eorCache == null || eorKey != key)
            {
                eorCache = new double[11];
                try
                {
                    var eng = new CAEngine(r);
                    var full = CAEngine.FullShoe(r.Decks);
                    double b = eng.Evaluate(full).PlayerEV;
                    for (int k = 1; k <= 10; k++)
                    {
                        var c = (double[])full.Clone();
                        c[k] -= 1;
                        eorCache[k] = eng.Evaluate(c).PlayerEV - b;
                    }
                }
                catch { }
                eorKey = key;
            }
            var cs = State.Count;
            double[] t = new double[11], ins = new double[11], w = new double[11];
            for (int k = 1; k <= 10; k++) { t[k] = cs.RankTag(k); w[k] = k == 10 ? 4 : 1; ins[k] = k == 10 ? -9.0 / 4.0 : 1; }
            bc = Corr(t, eorCache, w);
            ic = Corr(t, ins, w);
        }

        static double Corr(double[] x, double[] y, double[] w)
        {
            double sw = 0, mx = 0, my = 0;
            for (int k = 1; k <= 10; k++) { sw += w[k]; mx += w[k] * x[k]; my += w[k] * y[k]; }
            mx /= sw; my /= sw;
            double sxy = 0, sxx = 0, syy = 0;
            for (int k = 1; k <= 10; k++)
            {
                double dx = x[k] - mx, dy = y[k] - my;
                sxy += w[k] * dx * dy; sxx += w[k] * dx * dx; syy += w[k] * dy * dy;
            }
            return sxx > 0 && syy > 0 ? sxy / Math.Sqrt(sxx * syy) : 0;
        }

        public override void OnActivated()
        {
            RefreshStrategy();
        }

        void RefreshStrategy() { Batch(RefreshStrategyCore); }


        void RefreshStrategyCore()
        {
            if (!Visible) return;
            loading = true;
            object sel = viewAt.SelectedItem;
            int prev = viewAt.SelectedIndex;
            viewAt.Items.Clear();
            viewAt.Items.Add("Basic strategy (off the top)");
            string m = State.Count.Metric == CountMetric.TrueCount ? "TC " : "RC ";
            for (int v = State.Strategy.IndexMin; v <= State.Strategy.IndexMax; v++)
                viewAt.Items.Add(m + (v > 0 ? "+" + v : v.ToString(CultureInfo.InvariantCulture)));
            int idx = sel != null ? viewAt.Items.IndexOf(sel) : -1;
            viewAt.SelectedIndex = idx >= 0 ? idx : (prev > 0 && prev < viewAt.Items.Count ? prev : 0);
            loading = false;
            RenderCharts();
        }

        void RenderCharts() { Batch(RenderChartsCore); }


        void RenderChartsCore()
        {
            PlayTable pt;
            try { pt = State.Table; }
            catch (Exception ex) { countInfo.Text = "Strategy error: " + ex.Message; return; }
            int? at = null;
            string label = "";
            if (viewAt.SelectedIndex > 0)
            {
                at = State.Strategy.IndexMin + viewAt.SelectedIndex - 1;
                label = Convert.ToString(viewAt.SelectedItem);
            }
            var list = StrategyBuilder.DescribeIndices(pt, State.Strategy.IndexMin, State.Strategy.IndexMax);
            hard.Render(pt, at, showIdx.Checked, list, label);
            soft.Render(pt, at, showIdx.Checked, list, label);
            pairs.Render(pt, at, showIdx.Checked, list, label);
            indexList.Rows.Clear();
            foreach (var i in list)
            {
                if (i.Condition == "always") continue;
                indexList.Rows.Add(i.Hand, i.Play, i.Condition);
            }
            if (indexList.Rows.Count == 0) indexList.Rows.Add("—", "Basic strategy only", "");
            GridUtil.FitHeight(indexList, 18);
        }
    }
}
