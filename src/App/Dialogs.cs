using System;
using System.Drawing;
using System.Windows.Forms;
using CalcBJ.Engine;

namespace CalcBJ.App
{
    internal class DialogBase : Form
    {
        protected readonly FieldGrid Fields = new FieldGrid();
        protected readonly Button Ok, Cancel;

        public DialogBase(string title, string okText)
        {
            Text = title;
            Font = Theme.Body;
            BackColor = Theme.CardBg;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false; MinimizeBox = false; ShowInTaskbar = false;
            StartPosition = FormStartPosition.CenterParent;
            AutoSize = true;
            AutoSizeMode = AutoSizeMode.GrowAndShrink;
            Padding = new Padding(Theme.S(16));
            var root = new TableLayoutPanel();
            root.ColumnCount = 1; root.AutoSize = true; root.Dock = DockStyle.Fill;
            root.Controls.Add(Fields, 0, 0);
            var buttons = new FlowLayoutPanel();
            buttons.FlowDirection = FlowDirection.RightToLeft;
            buttons.AutoSize = true; buttons.Dock = DockStyle.Fill; buttons.Margin = new Padding(0, Theme.S(12), 0, 0);
            Cancel = Theme.Secondary("Cancel");
            Cancel.DialogResult = DialogResult.Cancel;
            Ok = Theme.Primary(okText);
            Ok.DialogResult = DialogResult.OK;
            buttons.Controls.Add(Cancel);
            buttons.Controls.Add(Ok);
            root.Controls.Add(buttons, 0, 1);
            Controls.Add(root);
            AcceptButton = Ok;
            CancelButton = Cancel;
        }

        protected override void OnLoad(EventArgs e)
        {
            UiLayout.Finish(this);
            base.OnLoad(e);
        }
    }

    internal sealed class OptimizeDialog : DialogBase
    {
        readonly RadioButton score, kelly;
        readonly NumericUpDown minU, maxU, inc, hands, frac;
        readonly CheckBox wong;

        public OptimizeDialog(double currentMax, double bankrollUnits)
            : base("Optimise bet ramp", "Optimise")
        {
            Fields.AddFull(Theme.Wrap("Finds the bet for every count from the simulated EV and variance at that count. "
                + "Bets are Kelly-proportional (EV ÷ variance), clipped to your limits and rounded to the increment.", 420));
            score = Fields.AddFull(new RadioButton());
            score.Text = "Maximise SCORE (best risk-adjusted win rate within the spread)";
            score.AutoSize = true; score.Checked = true;
            kelly = Fields.AddFull(new RadioButton());
            kelly.Text = "Kelly bets for my bankroll (" + bankrollUnits.ToString("#,0") + " units)";
            kelly.AutoSize = true;
            minU = Fields.Add("Minimum bet (units)", Theme.Num(0.25m, 1000, 1, 2, 0.25m, 90));
            maxU = Fields.Add("Maximum bet (units)", Theme.Num(0.25m, 10000, (decimal)Math.Max(1, currentMax), 2, 1, 90));
            inc = Fields.Add("Bet increment (units)", Theme.Num(0.05m, 100, 1, 2, 0.25m, 90));
            hands = Fields.Add("Max hands per round", Theme.Num(1, 3, 1, 0, 1, 90));
            frac = Fields.Add("Kelly fraction", Theme.Num(0.05m, 2, 1, 2, 0.05m, 90));
            wong = Fields.AddFull(Theme.Check("Sit out (wong out) when a hand isn't worth its risk"));
            kelly.CheckedChanged += (s, e) => frac.Enabled = kelly.Checked;
            frac.Enabled = false;
        }

        public OptimizerSettings Settings(double bankrollUnits)
        {
            var o = new OptimizerSettings();
            o.Kelly = kelly.Checked;
            o.MinUnits = (double)minU.Value;
            o.MaxUnits = Math.Max(o.MinUnits, (double)maxU.Value);
            o.Increment = (double)inc.Value;
            o.MaxHands = (int)hands.Value;
            o.KellyFraction = (double)frac.Value;
            o.AllowWongOut = wong.Checked;
            o.BankrollUnits = bankrollUnits;
            return o;
        }
    }

    internal sealed class GenerateDialog : DialogBase
    {
        readonly NumericUpDown start, below, step, max, wongAt;
        readonly CheckBox wong;

        public GenerateDialog(bool runningCount)
            : base("Generate linear ramp", "Generate")
        {
            string c = runningCount ? "running count" : "true count";
            Fields.AddFull(Theme.Wrap("Bets ramp up linearly from the starting " + c + ".", 380));
            start = Fields.Add("Start raising at " + c, Theme.Num(-100, 100, 1, 0, 1, 80));
            below = Fields.Add("Units below the start", Theme.Num(0, 1000, 1, 2, 0.5m, 80));
            step = Fields.Add("Extra units per +1", Theme.Num(0, 1000, 2, 2, 0.5m, 80));
            max = Fields.Add("Maximum units", Theme.Num(0.5m, 10000, 12, 2, 1, 80));
            wong = Fields.AddFull(Theme.Check("Sit out at low counts"));
            wongAt = Fields.Add("Sit out at count ≤", Theme.Num(-100, 100, -2, 0, 1, 80));
        }

        public void Apply(BetRamp r)
        {
            int s0 = (int)start.Value;
            double b = (double)below.Value, st = (double)step.Value, mx = (double)max.Value;
            for (int v = r.Min; v <= r.Max; v++)
            {
                double u = v < s0 ? b : Math.Min(mx, b + st * (v - s0 + 1));
                if (wong.Checked && v <= (int)wongAt.Value) u = 0;
                r.Units[v - r.Min] = u;
                r.Hands[v - r.Min] = 1;
            }
        }
    }
}
