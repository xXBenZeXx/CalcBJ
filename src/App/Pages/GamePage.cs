using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.Windows.Forms;
using CalcBJ.Engine;

namespace CalcBJ.App
{
    internal sealed class GamePage : PageBase
    {
        readonly ComboBox decks, soft17, hole, bj, dbl, splits, surrender;
        readonly NumericUpDown pen, spots, rph;
        readonly CheckBox das, rsa, hsa, burn, insurance, autoRph;
        readonly Label penInfo, heBig, heSub, spotsInfo;
        readonly DataGridView effects;
        static readonly double[] Payouts = { 1.5, 1.4, 1.2, 1.0, 2.0 };
        // A payout from a file that isn't one of these gets an entry of its own while it is in use.
        double otherPayout = 1.5;
        bool loading;

        public GamePage(AppState s)
            : base(s, "Game rules", "Rules, shoe and table. The basic-strategy house edge is recalculated instantly.", 54, 46)
        {
            var left = Column(0);
            var right = Column(1);

            var shoe = Stack(left, new Card("Shoe"));
            var g = shoe.AddRow(new FieldGrid());
            decks = g.Add("Number of decks", Theme.Combo(150, "1", "2", "3", "4", "5", "6", "7", "8"));
            pen = g.Add("Penetration (% dealt)", Theme.Num(20, 95, 75, 1, 0.5m, 90));
            penInfo = g.AddFull(Theme.MutedLbl(""));
            burn = g.AddFull(Theme.Check("Burn one card after each shuffle"));

            var dealer = Stack(left, new Card("Dealer"));
            g = dealer.AddRow(new FieldGrid());
            soft17 = g.Add("Soft 17", Theme.Combo(260, "Dealer stands on soft 17 (S17)", "Dealer hits soft 17 (H17)"));
            hole = g.Add("Hole card", Theme.Combo(260, "US peek (hole card)", "No hole card, lose all (ENHC)", "No hole card, original bets only (OBO)"));
            insurance = g.AddFull(Theme.Check("Insurance / even money offered"));

            var player = Stack(left, new Card("Player options"));
            g = player.AddRow(new FieldGrid());
            bj = g.Add("Blackjack pays", Theme.Combo(150, "3:2", "7:5", "6:5", "1:1", "2:1"));
            dbl = g.Add("Double down on", Theme.Combo(200, "Any two cards", "9, 10, 11 only", "10, 11 only"));
            das = g.AddFull(Theme.Check("Double after split (DAS)"));
            splits = g.Add("Split up to", Theme.Combo(150, "No splitting", "2 hands", "3 hands", "4 hands"));
            rsa = g.AddFull(Theme.Check("Resplit aces (RSA)"));
            hsa = g.AddFull(Theme.Check("Hit split aces (HSA)"));
            surrender = g.Add("Surrender", Theme.Combo(200, "Not allowed", "Late surrender", "Early surrender"));

            var table = Stack(left, new Card("Table"));
            g = table.AddRow(new FieldGrid());
            spots = g.Add("Spots dealt per round", Theme.Num(1, 7, 3, 0, 1, 70));
            spotsInfo = g.AddFull(Theme.Wrap("", 460));
            rph = g.Add("Rounds per hour", Theme.Num(10, 400, 105, 0, 5, 90));
            autoRph = g.AddFull(Theme.Check("Estimate rounds per hour from the number of spots"));

            var he = Stack(right, new Card("Basic strategy expectation (off the top)"));
            heBig = he.AddRow(new Label());
            heBig.Font = Theme.Huge; heBig.AutoSize = true; heBig.ForeColor = Theme.Text; heBig.Margin = new Padding(0, Theme.S(2), 0, 0);
            heSub = he.AddRow(Theme.Wrap("", 520));

            var eff = Stack(right, new Card("Effect of changing one rule"));
            eff.AddRow(Theme.Wrap("Change in player expectation if only this rule were different (combinatorial analysis, basic strategy).", 520));
            effects = eff.AddRow(new DataGridView());
            Theme.StyleGrid(effects);
            effects.ReadOnly = true;
            effects.Columns.Add(Theme.TextCol("Rule change", 250, false, true));
            effects.Columns.Add(Theme.TextCol("Player EV", 90, true, true));
            effects.Columns.Add(Theme.TextCol("Effect", 90, true, true));
            effects.Columns[0].AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill;
            effects.Dock = DockStyle.Fill;
            effects.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
            GridUtil.NoSelection(effects);

            Load();
            Hook();
            state_ConfigChanged(null, EventArgs.Empty);
            State.ConfigChanged += state_ConfigChanged;
            State.Reloaded += (o, e) => { Load(); state_ConfigChanged(null, EventArgs.Empty); };
        }

        void Hook()
        {
            EventHandler h = (o, e) => Save();
            decks.SelectedIndexChanged += h; soft17.SelectedIndexChanged += h; hole.SelectedIndexChanged += h;
            bj.SelectedIndexChanged += h; dbl.SelectedIndexChanged += h; splits.SelectedIndexChanged += h; surrender.SelectedIndexChanged += h;
            pen.ValueChanged += h; spots.ValueChanged += h; rph.ValueChanged += h;
            das.CheckedChanged += h; rsa.CheckedChanged += h; hsa.CheckedChanged += h; burn.CheckedChanged += h;
            insurance.CheckedChanged += h; autoRph.CheckedChanged += h;
        }

        void Load()
        {
            loading = true;
            var r = State.Rules;
            decks.SelectedIndex = Math.Max(0, Math.Min(7, r.Decks - 1));
            pen.Value = (decimal)Math.Max(20, Math.Min(95, Math.Round(r.Penetration * 100, 1)));
            burn.Checked = r.BurnCard;
            soft17.SelectedIndex = r.H17 ? 1 : 0;
            hole.SelectedIndex = (int)r.HoleCard;
            insurance.Checked = r.Insurance;
            int bi = Array.IndexOf(Payouts, r.BlackjackPays);
            while (bj.Items.Count > Payouts.Length) bj.Items.RemoveAt(bj.Items.Count - 1);
            if (bi < 0) { otherPayout = r.BlackjackPays; bj.Items.Add(Rules.PayoutText(otherPayout)); bi = Payouts.Length; }
            bj.SelectedIndex = bi;
            dbl.SelectedIndex = (int)r.DoubleOn;
            das.Checked = r.DAS;
            splits.SelectedIndex = Math.Max(0, Math.Min(3, r.MaxSplitHands - 1));
            rsa.Checked = r.ResplitAces;
            hsa.Checked = r.HitSplitAces;
            surrender.SelectedIndex = (int)r.Surrender;
            spots.Value = State.Spots;
            autoRph.Checked = State.AutoRoundsPerHour;
            rph.Value = (decimal)Math.Max(10, Math.Min(400, State.RoundsPerHour));
            rph.Enabled = !State.AutoRoundsPerHour;
            loading = false;
        }

        void Save()
        {
            if (loading) return;
            var r = State.Rules;
            var before = r.Clone();
            r.Decks = decks.SelectedIndex + 1;
            r.Penetration = (double)pen.Value / 100.0;
            r.BurnCard = burn.Checked;
            r.H17 = soft17.SelectedIndex == 1;
            r.HoleCard = (HoleCardRule)hole.SelectedIndex;
            r.Insurance = insurance.Checked;
            r.BlackjackPays = bj.SelectedIndex >= Payouts.Length ? otherPayout : Payouts[Math.Max(0, bj.SelectedIndex)];
            r.DoubleOn = (DoubleRule)dbl.SelectedIndex;
            r.DAS = das.Checked;
            r.MaxSplitHands = splits.SelectedIndex + 1;
            r.ResplitAces = rsa.Checked;
            r.HitSplitAces = hsa.Checked;
            r.Surrender = (SurrenderRule)surrender.SelectedIndex;
            State.Spots = (int)spots.Value;
            State.AutoRoundsPerHour = autoRph.Checked;
            rph.Enabled = !autoRph.Checked;
            if (State.AutoRoundsPerHour)
            {
                State.RoundsPerHour = AppState.DefaultRoundsPerHour(State.Spots);
                loading = true; rph.Value = (decimal)State.RoundsPerHour; loading = false;
            }
            else State.RoundsPerHour = (double)rph.Value;
            if (AppState.DependsOnDecks(State.Count))
            {
                // Count values mean other true counts now. With other decks the ramp moves with them (a preset is applied
                // again, a custom ramp keeps its bets at the same true counts) and the index range is set anew. With another
                // penetration only a preset follows; a custom ramp stays as it was set.
                if (r.Decks != before.Decks) { State.CarryRamp(State.Count, before); State.ResetIndexRange(); }
                else if (r.Penetration != before.Penetration && State.RampPreset >= 0 && State.RampPreset < RampPresets.Custom)
                    State.CarryRamp(State.Count, before);
            }
            State.NotifyConfig();
            State.NotifyBetting();
        }

        void state_ConfigChanged(object sender, EventArgs e) { Batch(ConfigChangedCore); }

        void ConfigChangedCore()
        {
            var r = State.Rules;
            int cut = r.CutCard(State.Spots);
            penInfo.Text = string.Format(CultureInfo.InvariantCulture, "Cut card after {0} of {1} cards  ·  {2:0.##} of {3} decks dealt",
                cut, r.TotalCards, cut / 52.0, r.Decks);
            spotsInfo.Text = State.Spots == 1
                ? "Heads-up. Covariance for playing two hands is then estimated (ρ ≈ 0.37)."
                : "You play the last seat (third base); the seat beside it is used when your ramp calls for two or three hands.";
            if (!loading && State.AutoRoundsPerHour && rph.Value != (decimal)State.RoundsPerHour)
            {
                loading = true; rph.Value = (decimal)State.RoundsPerHour; loading = false;
            }
            RefreshEdge();
        }

        void RefreshEdge() { Batch(RefreshEdgeCore); }


        void RefreshEdgeCore()
        {
            var r = State.Rules;
            double ev;
            try { ev = CAEngine.HouseEdgeOffTop(r); }
            catch (Exception ex) { heBig.Text = "—"; heSub.Text = ex.Message; return; }
            if (ev <= 0)
            {
                heBig.Text = Theme.Pct(-ev, 3);
                heBig.ForeColor = Theme.Bad;
                heSub.Text = "House edge for a perfect basic-strategy player on the first round of a fresh shoe "
                    + "(player expectation " + Theme.SignedPct(ev, 3) + ").";
            }
            else
            {
                heBig.Text = "+" + Theme.Pct(ev, 3);
                heBig.ForeColor = Theme.Good;
                heSub.Text = "Player edge for a perfect basic-strategy player on the first round of a fresh shoe. With a cut card "
                    + "a flat bet does a little worse than that (about 0.1% with one deck): simulate basic strategy to see it.";
            }

            var rows = new List<KeyValuePair<string, Rules>>();
            Rules t;
            t = r.Clone(); t.H17 = !r.H17; rows.Add(Pair(r.H17 ? "Dealer stands on soft 17" : "Dealer hits soft 17", t));
            foreach (int d in new[] { 1, 2, 4, 6, 8 })
                if (d != r.Decks) { t = r.Clone(); t.Decks = d; rows.Add(Pair(d + (d == 1 ? " deck" : " decks"), t)); }
            t = r.Clone(); t.DAS = !r.DAS; rows.Add(Pair(r.DAS ? "No double after split" : "Double after split allowed", t));
            string[] surr = { "No surrender", "Late surrender", "Early surrender" };
            for (int i = 0; i < 3; i++)
                if (i != (int)r.Surrender) { t = r.Clone(); t.Surrender = (SurrenderRule)i; rows.Add(Pair(surr[i], t)); }
            string[] dbls = { "Double on any two cards", "Double on 9–11 only", "Double on 10–11 only" };
            for (int i = 0; i < 3; i++)
                if (i != (int)r.DoubleOn) { t = r.Clone(); t.DoubleOn = (DoubleRule)i; rows.Add(Pair(dbls[i], t)); }
            for (int m = 2; m <= 4; m++)
                if (m != r.MaxSplitHands) { t = r.Clone(); t.MaxSplitHands = m; rows.Add(Pair("Split to " + m + " hands", t)); }
            t = r.Clone(); t.ResplitAces = !r.ResplitAces; rows.Add(Pair(r.ResplitAces ? "No resplitting aces" : "Resplit aces allowed", t));
            t = r.Clone(); t.HitSplitAces = !r.HitSplitAces; rows.Add(Pair(r.HitSplitAces ? "No hitting split aces" : "Hit split aces allowed", t));
            foreach (double p in new[] { 1.5, 1.2, 1.4 })
                if (Math.Abs(p - r.BlackjackPays) > 1e-9) { t = r.Clone(); t.BlackjackPays = p; rows.Add(Pair("Blackjack pays " + Rules.PayoutText(p), t)); }
            string[] holes = { "US peek", "No hole card (ENHC)", "No hole card, OBO" };
            for (int i = 0; i < 3; i++)
                if (i != (int)r.HoleCard) { t = r.Clone(); t.HoleCard = (HoleCardRule)i; rows.Add(Pair(holes[i], t)); }

            effects.Rows.Clear();
            foreach (var kv in rows)
            {
                double e2 = CAEngine.HouseEdgeOffTop(kv.Value);
                int idx = effects.Rows.Add(kv.Key, Theme.SignedPct(e2, 3), Theme.SignedPct(e2 - ev, 3));
                var cell = effects.Rows[idx].Cells[2];
                cell.Style.ForeColor = e2 - ev > 0.00005 ? Theme.Good : (e2 - ev < -0.00005 ? Theme.Bad : Theme.Muted);
                cell.Style.Font = Theme.BodyBold;
            }
            GridUtil.FitHeight(effects, 40);
        }

        static KeyValuePair<string, Rules> Pair(string s, Rules r) { return new KeyValuePair<string, Rules>(s, r); }
    }
}
