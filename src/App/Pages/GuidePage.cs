using System;
using System.Drawing;
using System.Windows.Forms;

namespace CalcBJ.App
{
    internal sealed class GuidePage : PageBase
    {
        public GuidePage(AppState s)
            : base(s, "Guide", "How CalcBJ works and what the numbers mean.", 50, 50)
        {
            var left = Column(0);
            var right = Column(1);
            Section(left, "Workflow",
                "1.  Game rules — enter the table rules. The basic-strategy house edge and the value of each rule update immediately.\n\n"
                + "2.  Count & strategy — pick a count (or type your own tags), the true-count method and which index plays you use. "
                + "Indices are generated for your exact count and rules; you can override any of them with your own numbers.\n\n"
                + "3.  Simulation — press F5. Each round is dealt from a real shoe to every spot; your seat's results are stored "
                + "for every count value. With auto-run on, any change re-simulates in the background.\n\n"
                + "4.  Bet ramp & results — change units and hands per count and everything updates instantly, without "
                + "re-simulating. The optimiser builds SCORE-maximising or Kelly ramps from the simulated EV and variance.\n\n"
                + "5.  Bankroll & risk, Charts, Compare — risk of ruin, trip outcomes and side-by-side comparisons. "
                + "Save scenarios (Ctrl+S) and export an HTML report.");
            Section(left, "How indices are computed",
                "An index is the count at the moment you decide, which includes your cards and the dealer's upcard. So for every "
                + "count value and hand, the program takes those cards out of the shoe and estimates the average make-up of the "
                + "remaining cards when the count, including the cards on the table, has that value (a linear regression of the "
                + "composition on the count, using your tags); how deep in the shoe that count usually comes up sets the number "
                + "of unseen cards. Then it runs a full combinatorial analysis of that composition: exact dealer probabilities with card "
                + "removal for every two-card hand, optimal hitting, doubling, splitting (with resplits) and surrender. The best "
                + "play at each count becomes the index. Index numbers follow your rounding method: with floored true counts "
                + "(TC 0 means 0 to 0.99) some come out a point higher than tables that assume exact counts; 16 v T, for "
                + "example, stands from +1.");
            Section(left, "Accuracy",
                "The basic strategy expectation off the top plays the computed strategy exactly, with card removal for every hit "
                + "and double, and agrees with shuffle-every-round simulations within two standard errors for 1 to 8 decks. "
                + "House edges match published figures (6D S17 DAS 0.41%, H17 0.62%). "
                + "Precision of a simulated win rate is shown as ± one standard error; use more rounds to shrink it. "
                + "Strategy is total-dependent (by hand total, not exact cards).");
            Section(right, "Glossary",
                "Win rate — expected profit per hour (or per 100 rounds) at your unit.\n\n"
                + "SD — standard deviation of results over an hour (or 100 rounds).\n\n"
                + "Player edge — win rate divided by the average initial amount wagered.\n\n"
                + "DI (desirability index) — 1000 × EV ÷ SD per round. Independent of bet size.\n\n"
                + "SCORE — win per 100 rounds for a $10,000 bankroll bet with optimal Kelly sizing; equals DI².\n\n"
                + "N0 — rounds needed before expectation equals one standard deviation: (SD ÷ EV)². After N0 rounds you are "
                + "ahead with about 84% probability.\n\n"
                + "Risk of ruin — chance of losing the whole bankroll if you never resize your bets: exp(−2·EV·B ÷ variance).\n\n"
                + "Kelly-optimal unit — unit size that maximises bankroll growth for this ramp shape: B × EV ÷ E[W²].\n\n"
                + "Certainty equivalent — risk-adjusted value of the game to a log-utility player with your bankroll: EV − variance ÷ (2B).\n\n"
                + "Covariance / multiple hands — when your ramp uses 2 or 3 hands, the variance includes the measured "
                + "covariance between neighbouring seats (≈ 0.48 units² per pair of hands).\n\n"
                + "Wonging — 0 units in a row means you sit out those counts; rates are still per observed round.");
            Section(right, "Shortcuts",
                "F5 run simulation   ·   Esc stop   ·   Ctrl+S save   ·   Ctrl+O open");
        }

        static void Section(VStack col, string title, string text)
        {
            var c = new Card(title);
            var l = new WrapLabel(Theme.S(900));
            l.Text = text;
            l.Font = Theme.Body;
            l.ForeColor = Theme.Text;
            l.Margin = new Padding(0, 0, 0, Theme.S(4));
            c.AddRow(l);
            col.Add(c);
        }
    }
}
