using System;
using System.Collections.Generic;
using System.Globalization;

namespace CalcBJ.Engine
{
    public static class Flag
    {
        public const byte Stand = 1;
        public const byte Double = 2;
        public const byte Surrender = 4;
        public const byte Split = 8;
    }

    public sealed class CAResult
    {
        /// <summary>Decision flags per cell (see PlayTable cell layout).</summary>
        public byte[] Flags;
        public bool Insure;
        /// <summary>
        /// Player expectation per initial unit for a round dealt from this composition (negative = house edge). NaN for an
        /// analysis at a count (CountModel): its deals are weighted by the shoe, not by the count.
        /// </summary>
        public double PlayerEV;
        /// <summary>Insurance EV per unit of original bet when the dealer shows an ace.</summary>
        public double InsuranceEV;
        /// <summary>
        /// Average EV of each option per cell: [cell*5 + k], k = stand, hit, double, surrender, split. NaN where not
        /// applicable. Counted from the deal: with an ace or ten up it includes the rounds lost to a dealer blackjack.
        /// </summary>
        public double[] CellEV;
        /// <summary>Per cell, the chance that the dealer has blackjack (0 unless an ace or a ten is up).</summary>
        public double[] CellBJ;

        /// <summary>True when the player decides knowing the dealer has no blackjack: the dealer peeks, and surrender isn't early.</summary>
        public static bool AfterPeek(Rules r)
        {
            return r.HoleCard == HoleCardRule.USPeek && r.Surrender != SurrenderRule.Early;
        }

        /// <summary>
        /// A cell's option value as the player meets it: in a game where the decision comes after the dealer's peek
        /// (AfterPeek), the value given that the dealer has no blackjack, so late surrender reads -0.5; otherwise from the
        /// deal.
        /// </summary>
        public double ShownEV(Rules r, int cell, int k)
        {
            double v = CellEV[cell * 5 + k];
            if (double.IsNaN(v) || CellBJ == null || !AfterPeek(r)) return v;
            double p = CellBJ[cell];
            return p > 0 && p < 1 ? (v + p) / (1 - p) : v;
        }
    }

    /// <summary>
    /// The unseen cards at a known count, for index plays. The player decides after seeing the upcard and their own two
    /// cards, so the count already includes them: for each deal, the cards left after that upcard and hand are shifted by
    /// the linear regression of the rank counts on the tag sum to the composition expected when the unseen cards' tags
    /// add up to UnseenTags.
    /// </summary>
    public sealed class CountModel
    {
        /// <summary>[1..10] average tag of each rank.</summary>
        public double[] Tags;
        /// <summary>[1..10] variance of the tags within each rank, where red and black cards count differently; null if none do.</summary>
        public double[] TagVar;
        /// <summary>Sum of the tags of the unseen cards.</summary>
        public double UnseenTags;
        /// <summary>Number of unseen cards.</summary>
        public double Unseen;
        /// <summary>
        /// How likely this count is with each tag sum of the three cards on the table (the upcard and the player's two),
        /// which the count includes: [(sum - TableMin) / TableStep], sums in the simulator's scaled tag units, largest
        /// entry 1. With one or two decks the hands of a total come up in very different shares at a count than off the
        /// top (T,2 is nine in ten of the 12s at Hi-Lo +1 with one deck). Null: as off the top.
        /// </summary>
        public double[] TableWeight;
        public int TableMin, TableStep = 1;
        /// <summary>[card code 0..19] tags in the simulator's scaled units (the two colours of each rank).</summary>
        public int[] CodeTags;
    }

    /// <summary>
    /// What exact card removal changes in the analysis, worked out off the top by CAEngine.BasicStrategy and added at
    /// every count: per deal (upcard u, first cards a &lt;= b) the change in the hit, double and split values, and per hard or
    /// soft cell how much more its hands of three or more cards prefer standing than its two-card hands (NaN where the
    /// strategy reaches no such hand).
    /// </summary>
    public sealed class ExactAdjust
    {
        public readonly double[] Hit = new double[11 * 11 * 11];
        public readonly double[] Dbl = new double[11 * 11 * 11];
        public readonly double[] Split = new double[11 * 11 * 11];
        public readonly double[] StandShift = new double[440];
        /// <summary>
        /// Per hard or soft cell: how often a hand of three or more cards plays it for each time a two-card hand does (0
        /// where none does, or where it wasn't worked out).
        /// </summary>
        public readonly double[] StandRatio = new double[440];
        /// <summary>Cards left after a deal off the top, where the corrections were measured.</summary>
        public double Cards;

        public static int Deal(int u, int a, int b) { return (u * 11 + a) * 11 + b; }
    }

    /// <summary>
    /// Combinatorial analysis for one shoe composition. The dealer distribution is exact (with removal) for every
    /// player two-card hand and upcard; the player's own draws use the composition left after those three cards.
    /// Decisions are total-dependent: each cell takes the action with the best probability-weighted EV.
    /// Not thread safe; use one engine per thread.
    /// </summary>
    public sealed class CAEngine
    {
        readonly Rules R;
        readonly DealerDag[] dags = new DealerDag[11];
        readonly double[] mass;
        readonly double[] dist = new double[7];
        readonly double[] sev = new double[22];
        readonly double[] V = new double[44];
        readonly double[] H = new double[44];
        readonly double[] p = new double[11];
        readonly double[] su = new double[11];
        readonly double[] sp = new double[11];
        readonly double[] pop = new double[11];
        readonly double[] tmp = new double[11];
        readonly double[] acc = new double[PlayTable.CellsPerBin * 4];
        readonly double[] accW = new double[PlayTable.CellsPerBin];
        readonly double[] accP = new double[PlayTable.CellsPerBin];
        readonly double[] pairAcc = new double[PlayTable.CellsPerBin * 2];

        struct Combo
        {
            public int U, A, B, Cell;
            /// <summary>W: probability of the deal; Wd: of the two cards given the upcard (the cell average's weight).</summary>
            public double W, Wd, S, Hh, D, Rr, P;
            /// <summary>Cards left after the deal (upcard and both cards out); scales the exact corrections.</summary>
            public double Np;
            /// <summary>Chance that the dealer has blackjack.</summary>
            public double Bj;
            public bool CanD;
        }
        readonly Combo[] combos = new Combo[600];
        readonly Combo[] cur = new Combo[600];
        int comboCount;
        // The rest of the last Evaluate that Decide needs.
        readonly byte[] baseFlags = new byte[PlayTable.CellsPerBin];
        double bjTotal, aceShare, insuranceEV;
        bool insure;

        public CAEngine(Rules rules)
        {
            R = rules.Clone();
            int maxStates = 0;
            for (int u = 1; u <= 10; u++)
            {
                dags[u] = DealerDag.Get(u, R.H17);
                maxStates = Math.Max(maxStates, dags[u].Count);
            }
            mass = new double[maxStates];
        }

        public static double[] FullShoe(double decks)
        {
            var c = new double[11];
            for (int r = 1; r <= 9; r++) c[r] = 4 * decks;
            c[10] = 16 * decks;
            return c;
        }

        void FillStand(double dBJ)
        {
            // dist[0..4] = 17..21, dist[5] = bust
            double below = dist[5] - dBJ - (dist[0] + dist[1] + dist[2] + dist[3] + dist[4]);
            for (int t = 0; t <= 16; t++) sev[t] = below;
            for (int t = 17; t <= 21; t++)
            {
                double ev = dist[5] - dBJ;
                for (int k = 0; k < 5; k++)
                {
                    int d = 17 + k;
                    if (t > d) ev += dist[k];
                    else if (t < d) ev -= dist[k];
                }
                sev[t] = ev;
            }
        }

        void FillDP()
        {
            for (int h = 21; h >= 2; h--)
            {
                for (int a = 0; a <= 1; a++)
                {
                    int best = (a == 1 && h + 10 <= 21) ? h + 10 : h;
                    double st = sev[best];
                    double hit = 0;
                    for (int r = 1; r <= 10; r++)
                    {
                        double pr = p[r];
                        if (pr == 0) continue;
                        int nh = h + r;
                        if (nh > 21) hit -= pr;
                        else hit += pr * V[nh * 2 + ((a == 1 || r == 1) ? 1 : 0)];
                    }
                    H[h * 2 + a] = hit;
                    V[h * 2 + a] = st > hit ? st : hit;
                }
            }
        }

        double Dbl(int h, int a)
        {
            double ev = 0;
            for (int r = 1; r <= 10; r++)
            {
                double pr = p[r];
                if (pr == 0) continue;
                int nh = h + r;
                if (nh > 21) ev -= 2 * pr;
                else
                {
                    int na = (a == 1 || r == 1) ? 1 : 0;
                    int best = (na == 1 && nh + 10 <= 21) ? nh + 10 : nh;
                    ev += 2 * pr * sev[best];
                }
            }
            return ev;
        }

        double SplitHandValue(int a, int r)
        {
            int h = a + r;
            int ace = (a == 1 || r == 1) ? 1 : 0;
            bool soft = ace == 1 && h + 10 <= 21;
            int best = soft ? h + 10 : h;
            if (a == 1 && !R.HitSplitAces) return sev[best];
            double v = Math.Max(sev[best], H[h * 2 + ace]);
            if (R.DAS && a != 1 && R.CanDoubleTotal(h, soft)) v = Math.Max(v, Dbl(h, ace));
            return v;
        }

        double ResplitDP(int pending, int hands, int maxHands, double pp, double x, double y)
        {
            if (pending == 0) return 0;
            double v = (1 - pp) * (x + ResplitDP(pending - 1, hands, maxHands, pp, x, y));
            if (hands < maxHands) v += pp * ResplitDP(pending + 1, hands + 1, maxHands, pp, x, y);
            else v += pp * (y + ResplitDP(pending - 1, hands, maxHands, pp, x, y));
            return v;
        }

        double SplitEV(int a)
        {
            int maxHands = Math.Min(R.MaxSplitHands, Rules.MaxSplitLimit);
            if (a == 1 && !R.ResplitAces) maxHands = 2;
            double pa = p[a];
            double sumX = 0;
            for (int r = 1; r <= 10; r++)
            {
                if (r == a || p[r] == 0) continue;
                sumX += p[r] * SplitHandValue(a, r);
            }
            double x = pa < 1 ? sumX / (1 - pa) : 0;
            double y = SplitHandValue(a, a);
            return ResplitDP(2, 2, maxHands, pa, x, y);
        }

        static double Sum(double[] c)
        {
            double n = 0;
            for (int r = 1; r <= 10; r++) n += c[r];
            return n;
        }

        /// <summary>
        /// The model's unseen cards out of the population left after a deal: q_r = n p_r (1 + (t_r - m) x) with
        /// x = (s/n - m) / V, where p, m and V are the population's rank shares, mean tag and tag variance per card. The
        /// variance includes the spread between a rank's red and black cards: the tag sum also depends on their colours,
        /// which the rank counts don't show. Returns the cards' sum.
        /// </summary>
        static double Regress(double[] popl, CountModel model, double[] outc)
        {
            double M = 0, st = 0;
            for (int r = 1; r <= 10; r++) { M += popl[r]; st += popl[r] * model.Tags[r]; }
            double n = Math.Min(model.Unseen, M);
            double mean = st / M, V = 0, maxDev = 0;
            for (int r = 1; r <= 10; r++)
            {
                if (popl[r] <= 0) continue;
                double d = model.Tags[r] - mean;
                V += model.TagVar != null ? popl[r] / M * (d * d + model.TagVar[r]) : popl[r] / M * d * d;
                maxDev = Math.Max(maxDev, Math.Abs(d));
            }
            double x = V > 1e-12 ? (model.UnseenTags / n - mean) / V : 0;
            if (maxDev > 1e-12)
            {
                double lim = 0.9 / maxDev;
                if (x > lim) x = lim; else if (x < -lim) x = -lim;
            }
            double total = 0;
            for (int r = 1; r <= 10; r++)
            {
                double f = 1 + (model.Tags[r] - mean) * x;
                if (f < 0.02) f = 0.02;
                double q = n * popl[r] / M * f;
                if (q > popl[r]) q = popl[r];
                outc[r] = q;
                total += q;
            }
            return total;
        }

        public CAResult Evaluate(double[] shoe)
        {
            return Evaluate(shoe, null, null);
        }

        public CAResult Evaluate(double[] shoe, CountModel model)
        {
            return Evaluate(shoe, model, null);
        }

        /// <summary>
        /// The player's draw odds from the cards c (n of them) left after the deal with upcard u. In a peek game with an
        /// ace or ten up the player draws only after the dealer has shown no blackjack, so the hole card is one of the
        /// cards that can't make one: P(next card is r) = (c_r - P(hole card is r)) / (n - 1).
        /// </summary>
        void DrawOdds(double[] c, double n, int u, bool peekCond)
        {
            for (int r = 1; r <= 10; r++) p[r] = c[r] / n;
            if (!peekCond || (u != 1 && u != 10) || n <= 1) return;
            int bj = u == 1 ? 10 : 1;
            double nb = n - c[bj];
            if (nb <= 1) return;
            for (int r = 1; r <= 10; r++) p[r] = r == bj ? c[r] / (n - 1) : c[r] * (nb - 1) / (nb * (n - 1));
        }

        /// <summary>
        /// Analysis of a round dealt from shoe. With a count model, shoe is the population before the deal and the cards
        /// each deal leaves unseen come from the model (the index plays at that count); deals are weighted by the shoe.
        /// adj (optional) adds what exact card removal changes in each deal's values (see BasicStrategy).
        /// </summary>
        public CAResult Evaluate(double[] shoe, CountModel model, ExactAdjust adj)
        {
            const int CPB = PlayTable.CellsPerBin;
            Array.Clear(baseFlags, 0, CPB);
            comboCount = 0;

            bool srBeforePeek = R.SurrenderBeforePeek;
            bool peekCond = !R.DealerBJTakesAll;
            bool canSplit = R.MaxSplitHands >= 2;
            double N = Sum(shoe);
            bjTotal = 0;
            aceShare = shoe[1] / N;
            insuranceEV = 0;
            insure = false;

            for (int u = 1; u <= 10; u++)
            {
                double pu = shoe[u] / N;
                for (int r = 1; r <= 10; r++) pop[r] = shoe[r];
                pop[u] = Math.Max(0, pop[u] - 1);
                double Npop = N - 1;
                double Nu;
                if (model == null)
                {
                    for (int r = 1; r <= 10; r++) su[r] = pop[r];
                    Nu = Npop;
                }
                else Nu = Regress(pop, model, su);

                // Hit/stand defaults for totals no two-card hand reaches (e.g. multi-card 21).
                dags[u].Evaluate(su, Nu, mass, dist);
                double pbj0 = dist[6];
                double dbj0 = 0;
                if (peekCond) { double nb = 1 - pbj0; if (nb > 1e-12) for (int k = 0; k < 6; k++) dist[k] /= nb; }
                else dbj0 = pbj0;
                FillStand(dbj0);
                DrawOdds(su, Nu, u, peekCond);
                FillDP();
                for (int t = 4; t <= 21; t++)
                {
                    baseFlags[PlayTable.HardCell(t, u)] = sev[t] >= H[t * 2] ? Flag.Stand : (byte)0;
                    if (t >= 12)
                        baseFlags[PlayTable.SoftCell(t, u)] = sev[t] >= H[(t - 10) * 2 + 1] ? Flag.Stand : (byte)0;
                }

                if (u == 1)
                {
                    double pT = su[10] / Nu;
                    insuranceEV = 1.5 * pT - 0.5;
                    insure = R.Insurance && pT > 1.0 / 3.0;
                }

                if (pu <= 0) continue;

                for (int a = 1; a <= 10; a++)
                {
                    double wa = pop[a] / Npop;
                    if (wa <= 0) continue;
                    for (int b = a; b <= 10; b++)
                    {
                        double sb = pop[b] - (a == b ? 1 : 0);
                        if (sb <= 0) continue;
                        double w = wa * sb / (Npop - 1) * (a == b ? 1 : 2);
                        double Np;
                        if (model == null)
                        {
                            for (int r = 1; r <= 10; r++) sp[r] = pop[r];
                            sp[a] = Math.Max(0, sp[a] - 1);
                            sp[b] = Math.Max(0, sp[b] - 1);
                            Np = Npop - 2;
                        }
                        else
                        {
                            for (int r = 1; r <= 10; r++) tmp[r] = pop[r];
                            tmp[a] = Math.Max(0, tmp[a] - 1);
                            tmp[b] = Math.Max(0, tmp[b] - 1);
                            Np = Regress(tmp, model, sp);
                        }
                        double pBJ = u == 1 ? sp[10] / Np : (u == 10 ? sp[1] / Np : 0);

                        if (a == 1 && b == 10)
                        {
                            bjTotal += pu * w * (1 - pBJ) * R.BlackjackPays;
                            continue;
                        }

                        dags[u].Evaluate(sp, Np, mass, dist);
                        double dBJ = 0;
                        if (peekCond) { double nb = 1 - dist[6]; if (nb > 1e-12) for (int k = 0; k < 6; k++) dist[k] /= nb; }
                        else dBJ = dist[6];
                        FillStand(dBJ);
                        DrawOdds(sp, Np, u, peekCond);
                        FillDP();

                        int h0 = a + b;
                        int ace0 = (a == 1 || b == 1) ? 1 : 0;
                        bool soft0 = ace0 == 1 && h0 + 10 <= 21;
                        int best0 = soft0 ? h0 + 10 : h0;
                        double evS = sev[best0];
                        double evH = H[h0 * 2 + ace0];
                        bool canD = R.CanDoubleTotal(h0, soft0);
                        double evD = canD ? Dbl(h0, ace0) : 0;
                        double evP = (a == b && canSplit) ? SplitEV(a) : 0;
                        if (peekCond && pBJ > 0)
                        {
                            double q = 1 - pBJ;
                            evS = -pBJ + q * evS; evH = -pBJ + q * evH; evD = -pBJ + q * evD; evP = -pBJ + q * evP;
                        }
                        double evR = srBeforePeek ? -0.5 : -pBJ - 0.5 * (1 - pBJ);

                        var c = new Combo();
                        c.U = u; c.A = a; c.B = b; c.W = w * pu; c.Wd = w; c.Np = Np; c.Bj = pBJ;
                        if (model != null && model.TableWeight != null) c.Wd = w * TableWeight(model, u, a, b);
                        c.Cell = soft0 ? PlayTable.SoftCell(best0, u) : PlayTable.HardCell(h0, u);
                        c.S = evS; c.Hh = evH; c.D = evD; c.Rr = evR; c.P = evP; c.CanD = canD;
                        combos[comboCount++] = c;
                    }
                }
            }
            return Decide(model == null, adj);
        }

        /// <summary>
        /// How likely the model's count is with this deal on the table, relative to the likeliest deal (the two colours of
        /// each rank averaged; never quite 0, so that no cell loses all its hands).
        /// </summary>
        static double TableWeight(CountModel m, int u, int a, int b)
        {
            int[] ct = m.CodeTags;
            double[] tw = m.TableWeight;
            double sum = 0;
            for (int i = 0; i < 2; i++)
                for (int j = 0; j < 2; j++)
                    for (int k = 0; k < 2; k++)
                    {
                        int idx = (ct[(u - 1) * 2 + i] + ct[(a - 1) * 2 + j] + ct[(b - 1) * 2 + k] - m.TableMin) / m.TableStep;
                        if (idx >= 0 && idx < tw.Length) sum += tw[idx];
                    }
            return Math.Max(sum / 8, 1e-9);
        }

        /// <summary>
        /// Flags, cell values and expectation from the deals of the last Evaluate (combos), with the exact corrections of
        /// adj added to each deal's hit, double and split values. Leaves combos as they are.
        /// </summary>
        CAResult Decide(bool offTop, ExactAdjust adj)
        {
            const int CPB = PlayTable.CellsPerBin;
            var flags = (byte[])baseFlags.Clone();
            var cellEV = new double[CPB * 5];
            for (int i = 0; i < cellEV.Length; i++) cellEV[i] = double.NaN;
            Array.Clear(acc, 0, acc.Length);
            Array.Clear(accW, 0, accW.Length);
            Array.Clear(accP, 0, accP.Length);
            var cellBJ = new double[CPB];
            bool surrAllowed = R.Surrender != SurrenderRule.None;
            bool canSplit = R.MaxSplitHands >= 2;

            for (int i = 0; i < comboCount; i++)
            {
                Combo c = combos[i];
                if (adj != null)
                {
                    // Removing a drawn card matters more the fewer cards are left: the corrections grow with 1 / cards left.
                    int k = ExactAdjust.Deal(c.U, c.A, c.B);
                    double sc = adj.Cards / c.Np;
                    c.Hh += sc * adj.Hit[k];
                    if (c.CanD) c.D += sc * adj.Dbl[k];
                    if (c.A == c.B && canSplit) c.P += sc * adj.Split[k];
                }
                cur[i] = c;
                int cell = c.Cell;
                acc[cell * 4] += c.Wd * c.S;
                acc[cell * 4 + 1] += c.Wd * c.Hh;
                if (c.CanD) acc[cell * 4 + 2] += c.Wd * c.D;
                if (surrAllowed) acc[cell * 4 + 3] += c.Wd * c.Rr;
                accW[cell] += c.Wd;
                accP[cell] += c.Wd * c.Bj;
            }

            var res = new CAResult();
            res.InsuranceEV = insuranceEV;
            res.Insure = insure;

            // Pass 1: hard and soft cells.
            for (int cell = 0; cell < 440; cell++)
            {
                double w = accW[cell];
                if (w <= 0) continue;
                int t = (cell % 220) / 10;
                bool soft = cell >= 220;
                bool canD = R.CanDoubleTotal(soft ? t - 10 : t, soft);
                double S = acc[cell * 4] / w, Hv = acc[cell * 4 + 1] / w;
                double D = canD ? acc[cell * 4 + 2] / w : double.NegativeInfinity;
                double Rv = surrAllowed ? acc[cell * 4 + 3] / w : double.NegativeInfinity;
                // The cell's stand-or-hit also plays its hands of three or more cards (ratio of them for each two-card
                // hand), which prefer standing by shift more than the two-card hands do (see ExactAdjust). So standing
                // is worth S plus what they gain by it, and where the two-card hands double or surrender, the stand-or-hit
                // is theirs alone. Without an adjustment both are 0 and the two-card hands decide.
                double ratio = 0, shift = 0;
                if (adj != null && adj.StandRatio[cell] > 0 && !double.IsNaN(adj.StandShift[cell]))
                {
                    ratio = adj.StandRatio[cell]; shift = adj.StandShift[cell];
                }
                double multi = S - Hv + shift;
                double vS = S + ratio * multi, own = ratio * Math.Max(multi, 0);
                bool stand = vS >= Hv;
                double bestHS = stand ? vS : Hv;
                byte f = 0;
                if (D + own > bestHS) f |= Flag.Double;
                if (Rv + own > Math.Max(bestHS, D + own)) f |= Flag.Surrender;
                if (f != 0 && ratio > 0) stand = multi >= 0;
                else if (f != 0) stand = S >= Hv;
                if (stand) f |= Flag.Stand;
                flags[cell] = f;
                cellBJ[cell] = accP[cell] / w;
                cellEV[cell * 5] = S; cellEV[cell * 5 + 1] = Hv;
                if (canD) cellEV[cell * 5 + 2] = D;
                if (surrAllowed) cellEV[cell * 5 + 3] = Rv;
            }

            // Pass 2: pairs (compare splitting with how the total would otherwise be played).
            for (int i = 0; i < comboCount; i++)
            {
                Combo c = cur[i];
                if (c.A != c.B) continue;
                int h0 = c.A + c.B;
                bool soft0 = c.A == 1;
                int tcell = soft0 ? PlayTable.SoftCell(12, c.U) : PlayTable.HardCell(h0, c.U);
                byte tf = flags[tcell];
                double nonSplit = ((tf & Flag.Double) != 0 && c.CanD) ? c.D : ((tf & Flag.Stand) != 0 ? c.S : c.Hh);
                int pcell = PlayTable.PairCell(c.A, c.U);
                byte f = 0;
                if (canSplit && c.P > nonSplit) f |= Flag.Split;
                double bestPlay = Math.Max(nonSplit, canSplit ? c.P : double.NegativeInfinity);
                if (surrAllowed && c.Rr > bestPlay) f |= Flag.Surrender;
                flags[pcell] = f;
                cellBJ[pcell] = c.Bj;
                cellEV[pcell * 5] = c.S;
                cellEV[pcell * 5 + 1] = c.Hh;
                if (c.CanD) cellEV[pcell * 5 + 2] = c.D;
                if (surrAllowed) cellEV[pcell * 5 + 3] = c.Rr;
                cellEV[pcell * 5 + 4] = canSplit ? c.P : double.NaN;
            }

            // Pass 3: expectation of the resulting strategy.
            double evTotal = bjTotal;
            for (int i = 0; i < comboCount; i++)
            {
                Combo c = cur[i];
                double ev;
                int h0 = c.A + c.B;
                bool soft0 = (c.A == 1 || c.B == 1) && h0 + 10 <= 21;
                byte pf = c.A == c.B ? flags[PlayTable.PairCell(c.A, c.U)] : (byte)0;
                byte tf = flags[soft0 ? PlayTable.SoftCell(h0 + 10, c.U) : PlayTable.HardCell(h0, c.U)];
                if (surrAllowed && (pf & Flag.Surrender) != 0) ev = c.Rr;
                else if ((pf & Flag.Split) != 0) ev = c.P;
                else if (surrAllowed && (tf & Flag.Surrender) != 0) ev = c.Rr;
                else if ((tf & Flag.Double) != 0 && c.CanD) ev = c.D;
                else ev = (tf & Flag.Stand) != 0 ? c.S : c.Hh;
                evTotal += c.W * ev;
            }
            if (res.Insure) evTotal += aceShare * res.InsuranceEV;

            res.Flags = flags;
            res.PlayerEV = offTop ? evTotal : double.NaN;
            res.CellEV = cellEV;
            res.CellBJ = cellBJ;
            return res;
        }

        /// <summary>
        /// Basic strategy off the top with exact card removal. Evaluate keeps the player's draw odds and the dealer's
        /// distribution as they are after the first three cards, which gets close plays wrong with one or two decks (e.g.
        /// 11 v A with 2 decks S17, A,2 v 4 with 1 deck, or splitting 2,2 v 3 with 1 deck). Here every hit (followed by the
        /// strategy's own play, as the simulator plays it), double and split of the two-card hands takes the drawn cards
        /// out of the shoe, as ExactOffTop does. In a cell whose two-card hands all double or surrender, the stand/hit
        /// fallback comes from the hands of three or more cards that actually use it (16 v T with surrender: stand). adj
        /// receives the differences to Evaluate's values, which StrategyBuilder.Build carries over to the analyses at each
        /// count. See BasicFor for the remembered results.
        /// </summary>
        public CAResult BasicStrategy(out ExactAdjust adj)
        {
            return BasicStrategy(out adj, false);
        }

        /// <summary>
        /// Splits whose approximate value is this far from the play they compete with keep it: the exact split value never
        /// moves by more than about 0.04 (1 deck), so the decision can't change.
        /// </summary>
        const double SplitMargin = 0.1;

        /// <summary>
        /// A cell whose stand, hit, double and surrender values are this far apart keeps its decision whatever its hands of
        /// three or more cards prefer (the light and the full basic strategy agree on 432 rule sets of 1 to 8 decks).
        /// </summary>
        const double CloseCall = 0.1;

        /// <summary>
        /// light: only what the decisions off the top need (for house edges), not the corrections for the analyses at a
        /// count. Exact split values only for pairs whose split is within SplitMargin of the alternative, and fallbacks only
        /// for the cells whose two-card hands double or surrender. The flags and the expectation are the same (checked on
        /// 216 rule sets of 1 to 8 decks).
        /// </summary>
        CAResult BasicStrategy(out ExactAdjust adj, bool light)
        {
            var shoe = FullShoe(R.Decks);
            double N = Sum(shoe);
            var first = Evaluate(shoe);
            int count = comboCount;
            var deals = new Combo[count];
            Array.Copy(combos, deals, count);
            adj = new ExactAdjust();
            adj.Cards = N - 3;
            BeginExact(first.Flags);
            bool peekCond = !R.DealerBJTakesAll;

            // Doubles with every drawn card out of the shoe (they don't depend on the strategy).
            for (int i = 0; i < count; i++)
            {
                Combo c = deals[i];
                if (!c.CanD) continue;
                double Np = SetDeal(shoe, N, c.U, c.A, c.B);
                double cut = peekCond ? DealBJ(c.U, Np) : 0;
                adj.Dbl[ExactAdjust.Deal(c.U, c.A, c.B)] = (Draw(WithCard(WithCard(0, c.A), c.B), Np, c.A + c.B, c.A == 1 || c.B == 1, 2, false) - cut) - c.D;
            }

            // Hits, then the strategy's own play, every card out of the shoe. The flags depend on these values in turn, so
            // repeat until they settle (one or two rounds in practice).
            CAResult res = first;
            for (int pass = 0; pass < 6; pass++)
            {
                BeginExact(res.Flags);
                for (int i = 0; i < count; i++)
                {
                    Combo c = deals[i];
                    double Np = SetDeal(shoe, N, c.U, c.A, c.B);
                    double cut = peekCond ? DealBJ(c.U, Np) : 0;
                    adj.Hit[ExactAdjust.Deal(c.U, c.A, c.B)] = (Draw(WithCard(WithCard(0, c.A), c.B), Np, c.A + c.B, c.A == 1 || c.B == 1, 1, false) - cut) - c.Hh;
                }
                var next = Decide(true, adj);
                bool same = SameFlags(next, res);
                res = next;
                if (same) break;
            }

            // Splits, played by those hard and soft flags.
            if (R.MaxSplitHands >= 2)
            {
                BeginExact(res.Flags);
                for (int i = 0; i < count; i++)
                {
                    Combo c = deals[i];
                    if (c.A != c.B) continue;
                    int k = ExactAdjust.Deal(c.U, c.A, c.B);
                    if (light)
                    {
                        // The play the split competes with (Decide's pass 2), with the exact hit and double values.
                        byte tf = res.Flags[c.A == 1 ? PlayTable.SoftCell(12, c.U) : PlayTable.HardCell(2 * c.A, c.U)];
                        double other = ((tf & Flag.Double) != 0 && c.CanD) ? c.D + adj.Dbl[k] : ((tf & Flag.Stand) != 0 ? c.S : c.Hh + adj.Hit[k]);
                        if (Math.Abs(c.P - other) >= SplitMargin) continue;
                    }
                    double Np = SetDeal(shoe, N, c.U, c.A, c.B);
                    adj.Split[k] = SplitExact(c.A, Np, DealBJ(c.U, Np), peekCond) - c.P;
                }
                res = Decide(true, adj);
            }

            // Stand/hit fallbacks of the cells whose two-card hands double or surrender. (A second round, with the hands
            // these fallbacks send to higher totals, changed no flag in 216 rule sets of 1 to 8 decks.)
            BeginExact(res.Flags);
            StandShift(shoe, N, deals, count, res, adj, light);
            return Decide(true, adj);
        }

        static bool SameFlags(CAResult a, CAResult b)
        {
            for (int i = 0; i < a.Flags.Length; i++) if (a.Flags[i] != b.Flags[i]) return false;
            return true;
        }

        /// <summary>Chance that the dealer's hole card gives a blackjack, from the cards in xc.</summary>
        double DealBJ(int u, double Np)
        {
            return u == 1 ? xc[10] / Np : (u == 10 ? xc[1] / Np : 0);
        }

        // Basic strategies of recently used rules, per thread: the Game page asks for the same rule variants on every
        // refresh, and settings such as the penetration don't change them.
        sealed class BasicEntry { public CAResult Result; public ExactAdjust Adj; public double OffTop; }
        [ThreadStatic] static Dictionary<string, BasicEntry> basicCache;
        [ThreadStatic] static Queue<string> basicOrder;
        const int BasicCacheLimit = 48;

        /// <summary>
        /// BasicStrategy and its exact off-the-top expectation (ExactOffTop) for the rules, remembered per thread for the
        /// last 48 rule sets. Returns a copy of the result, so callers may change it; adj is shared and must not be changed.
        /// </summary>
        public static CAResult BasicFor(Rules rules, out ExactAdjust adj, out double offTop)
        {
            return BasicFor(rules, false, out adj, out offTop);
        }

        /// <summary>The rules that decide basic strategy and its expectation (not the penetration or the burn card).</summary>
        static string BasicKey(Rules rules)
        {
            return string.Format(CultureInfo.InvariantCulture, "{0}|{1}|{2}|{3:R}|{4}|{5}|{6}|{7}|{8}|{9}|{10}",
                rules.Decks, rules.H17, rules.HoleCard, rules.BlackjackPays, rules.DoubleOn, rules.DAS, rules.MaxSplitHands,
                rules.ResplitAces, rules.HitSplitAces, rules.Surrender, rules.Insurance);
        }

        /// <summary>BasicFor; light leaves out the corrections for the analyses at a count (see BasicStrategy).</summary>
        static CAResult BasicFor(Rules rules, bool light, out ExactAdjust adj, out double offTop)
        {
            string key = BasicKey(rules) + (light ? "|light" : "|full");
            if (basicCache == null) { basicCache = new Dictionary<string, BasicEntry>(); basicOrder = new Queue<string>(); }
            BasicEntry e;
            // A full result serves a light request as well (same flags and expectation).
            if (light && basicCache.TryGetValue(key.Substring(0, key.Length - 5) + "full", out e)) { }
            else if (!basicCache.TryGetValue(key, out e))
            {
                var eng = new CAEngine(rules);
                e = new BasicEntry();
                e.Result = eng.BasicStrategy(out e.Adj, light);
                e.OffTop = eng.ExactOffTop(e.Result);
                if (basicCache.Count >= BasicCacheLimit) basicCache.Remove(basicOrder.Dequeue());
                basicCache[key] = e;
                basicOrder.Enqueue(key);
            }
            adj = e.Adj;
            offTop = e.OffTop;
            var copy = new CAResult();
            copy.Flags = (byte[])e.Result.Flags.Clone();
            copy.Insure = e.Result.Insure;
            copy.PlayerEV = e.Result.PlayerEV;
            copy.InsuranceEV = e.Result.InsuranceEV;
            copy.CellEV = (double[])e.Result.CellEV.Clone();
            copy.CellBJ = (double[])e.Result.CellBJ.Clone();
            return copy;
        }

        /// <summary>Puts the cards left after the deal (upcard u, cards a and b) in xc; returns their number.</summary>
        double SetDeal(double[] shoe, double N, int u, int a, int b)
        {
            xUp = u;
            for (int r = 1; r <= 10; r++) xc[r] = shoe[r];
            xc[u] = Math.Max(0, xc[u] - 1);
            xc[a] = Math.Max(0, xc[a] - 1);
            xc[b] = Math.Max(0, xc[b] - 1);
            return N - 3;
        }

        /// <summary>
        /// For every hard and soft cell: how much more the hands of three or more cards that play it prefer standing than
        /// its two-card hands do. That is the mean of stand minus hit-and-play-on over the multi-card hands the strategy
        /// (xFlags) reaches by hitting, every card out of the shoe and each hand weighted by how often it is reached, minus
        /// the cell's two-card stand-minus-hit (basic.CellEV). Split hands are left out. Hands are visited by number of
        /// cards and in key order, so the sums don't depend on hashing.
        /// </summary>
        void StandShift(double[] shoe, double N, Combo[] deals, int count, CAResult basic, ExactAdjust adj, bool light)
        {
            var gain = new double[440];
            var reach = new double[440];
            var two = new double[440];
            for (int i = 0; i < count; i++) two[deals[i].Cell] += deals[i].W;
            // light: only the cells where the hands of three or more cards can change a decision, which are those whose
            // two-card hands double or surrender and those with a close call.
            bool[] skip = null;
            if (light)
            {
                skip = new bool[440];
                for (int cell = 0; cell < 440; cell++)
                {
                    if ((xFlags[cell] & (Flag.Double | Flag.Surrender)) != 0) continue;
                    double s2 = basic.CellEV[cell * 5], h2 = basic.CellEV[cell * 5 + 1], d2 = basic.CellEV[cell * 5 + 2], r2 = basic.CellEV[cell * 5 + 3];
                    if (double.IsNaN(s2) || double.IsNaN(h2)) continue;
                    double best = Math.Max(s2, h2);
                    skip[cell] = Math.Abs(s2 - h2) >= CloseCall && !(best - d2 < CloseCall) && !(best - r2 < CloseCall);
                }
            }
            bool surrAllowed = R.Surrender != SurrenderRule.None;
            var level = new Dictionary<long, double>();
            var next = new Dictionary<long, double>();
            var keys = new List<long>();
            for (int u = 1; u <= 10; u++)
            {
                level.Clear();
                // Two-card hands that hit: their third card starts the multi-card hands.
                for (int i = 0; i < count; i++)
                {
                    Combo c = deals[i];
                    if (c.U != u) continue;
                    int h0 = c.A + c.B;
                    bool soft0 = (c.A == 1 || c.B == 1) && h0 + 10 <= 21;
                    byte pf = c.A == c.B ? xFlags[PlayTable.PairCell(c.A, u)] : (byte)0;
                    byte tf = xFlags[soft0 ? PlayTable.SoftCell(h0 + 10, u) : PlayTable.HardCell(h0, u)];
                    if (surrAllowed && (pf & Flag.Surrender) != 0) continue;
                    if ((pf & Flag.Split) != 0) continue;
                    if (surrAllowed && (tf & Flag.Surrender) != 0) continue;
                    if ((tf & Flag.Double) != 0 && c.CanD) continue;
                    if ((tf & Flag.Stand) != 0) continue;
                    double Np = SetDeal(shoe, N, u, c.A, c.B);
                    long cards = WithCard(WithCard(0, c.A), c.B);
                    for (int r = 1; r <= 10; r++)
                    {
                        if (xc[r] <= 0 || h0 + r > 21) continue;
                        long key = WithCard(cards, r);
                        double v;
                        level.TryGetValue(key, out v);
                        level[key] = v + c.W * (xc[r] / Np);
                    }
                }
                while (level.Count > 0)
                {
                    next.Clear();
                    keys.Clear();
                    keys.AddRange(level.Keys);
                    keys.Sort();
                    foreach (long cards in keys)
                    {
                        double rch = level[cards];
                        int h = 0, held = 0;
                        for (int r = 1; r <= 10; r++)
                        {
                            int m = (int)((cards >> (5 * (r - 1))) & 31);
                            xc[r] = shoe[r] - (r == u ? 1 : 0) - m;
                            h += r * m;
                            held += m;
                        }
                        bool ace = ((cards & 31) != 0);
                        double n = N - 1 - held;
                        xUp = u;
                        bool soft = ace && h <= 11;
                        int best = soft ? h + 10 : h;
                        if (best == 21) continue;
                        int cell = soft ? PlayTable.SoftCell(best, u) : PlayTable.HardCell(h, u);
                        if (skip == null || !skip[cell])
                        {
                            double st = Terminal(cards, n, best, 1);
                            double hit = Draw(cards, n, h, ace, 1, false);
                            gain[cell] += rch * (st - hit);
                            reach[cell] += rch;
                        }
                        if ((xFlags[cell] & Flag.Stand) != 0) continue;
                        for (int r = 1; r <= 10; r++)
                        {
                            if (xc[r] <= 0 || h + r > 21) continue;
                            long key = WithCard(cards, r);
                            double v;
                            next.TryGetValue(key, out v);
                            next[key] = v + rch * (xc[r] / n);
                        }
                    }
                    var t = level; level = next; next = t;
                }
            }
            for (int cell = 0; cell < 440; cell++)
            {
                adj.StandShift[cell] = double.NaN;
                adj.StandRatio[cell] = 0;
                double s2 = basic.CellEV[cell * 5], h2 = basic.CellEV[cell * 5 + 1];
                if (reach[cell] <= 0 || two[cell] <= 0 || double.IsNaN(s2) || double.IsNaN(h2)) continue;
                adj.StandShift[cell] = gain[cell] / reach[cell] - (s2 - h2);
                adj.StandRatio[cell] = reach[cell] / two[cell];
            }
        }

        /// <summary>Off-the-top player expectation for the rules with optimal total-dependent basic strategy (BasicStrategy),
        /// played as the simulator plays it (see ExactOffTop).</summary>
        public static double HouseEdgeOffTop(Rules rules)
        {
            ExactAdjust adj;
            double offTop;
            BasicFor(rules, true, out adj, out offTop);
            return offTop;
        }

        // ---------------- Exact expectation of a strategy off the top ----------------

        // Raw dealer distributions keyed by game, upcard and the player's cards. Shared by the engines of a thread: the
        // Game page asks for many rule variants in a row, and most of them deal the same cards. One game (number of decks
        // and soft-17 rule) needs about 26,000 of them; the limit holds the six games of a Game page refresh.
        [ThreadStatic] static Dictionary<long, double[]> dealerCache;
        const int DealerCacheLimit = 1 << 18;

        readonly double[] xc = new double[11];
        readonly Dictionary<long, double> handMemo = new Dictionary<long, double>();
        readonly Dictionary<long, double> splitMemo = new Dictionary<long, double>();
        byte[] xFlags;
        int xUp;
        long xGame;
        bool xTakesAll;

        // The split being worked out: pair rank, resplits so far (pair cards out besides the two dealt), most hands, and
        // per number of resplits the chance of drawing the pair rank and the values of a hand without and with it.
        int sRank, sLevel, sMaxHands;
        readonly double[] splitP = new double[3];
        readonly double[] splitX = new double[3];
        readonly double[] splitY = new double[3];

        /// <summary>Adds a card of rank r to a hand key (5 bits per rank).</summary>
        static long WithCard(long cards, int r)
        {
            return cards + (1L << (5 * (r - 1)));
        }

        // The dealer distributions of a shoe that isn't the full one (EffectsOfRemoval); null for the shared cache.
        Dictionary<long, double[]> ownDealer;

        double[] RawDealer(long cards, double n)
        {
            var cache = ownDealer ?? dealerCache;
            long key = ((cards * 16 + xUp) << 5) + xGame;
            double[] d;
            if (cache.TryGetValue(key, out d)) return d;
            dags[xUp].Evaluate(xc, n, mass, dist);
            d = (double[])dist.Clone();
            if (cache.Count >= DealerCacheLimit) cache.Clear();
            cache[key] = d;
            return d;
        }

        /// <summary>The player stands on total with bet units; peek games leave out the dealer's blackjacks (see ExactOffTop).</summary>
        double Terminal(long cards, double n, int total, int bet)
        {
            double[] d = RawDealer(cards, n);
            double ev = d[5];
            for (int k = 0; k < 5; k++)
            {
                int t = 17 + k;
                if (total > t) ev += d[k];
                else if (total < t) ev -= d[k];
            }
            ev *= bet;
            if (xTakesAll) ev -= bet * d[6];
            return ev;
        }

        double Bust(double n, int bet)
        {
            if (xTakesAll) return -bet;
            double pb = xUp == 1 ? xc[10] / n : (xUp == 10 ? xc[1] / n : 0);
            return -bet * (1 - pb);
        }

        /// <summary>
        /// Value of a hand of three or more cards, played by the flags as the simulator plays it. In a split hand, cards
        /// also holds the cards of the split that are out of the shoe but not in this hand (see SplitExact).
        /// </summary>
        double Play(long cards, double n, int h, bool ace, bool split)
        {
            long key = split ? ((cards * 16 + xUp) * 16 + sRank) * 4 + sLevel : cards * 16 + xUp;
            var memo = split ? splitMemo : handMemo;
            double v;
            if (memo.TryGetValue(key, out v)) return v;
            bool soft = ace && h <= 11;
            int best = soft ? h + 10 : h;
            if (best == 21) v = Terminal(cards, n, 21, 1);
            else
            {
                int cell = soft ? PlayTable.SoftCell(best, xUp) : PlayTable.HardCell(h, xUp);
                v = (xFlags[cell] & Flag.Stand) != 0 ? Terminal(cards, n, best, 1) : Draw(cards, n, h, ace, 1, split);
            }
            memo[key] = v;
            return v;
        }

        /// <summary>Takes one card: a hit (bet 1, then plays on) or a double (bet 2, then stands).</summary>
        double Draw(long cards, double n, int h, bool ace, int bet, bool split)
        {
            double ev = 0;
            for (int r = 1; r <= 10; r++)
            {
                double cr = xc[r];
                if (cr <= 0) continue;
                double pr = cr / n;
                xc[r] = cr - 1;
                int h2 = h + r;
                bool a2 = ace || r == 1;
                double v;
                if (h2 > 21) v = Bust(n - 1, bet);
                else if (bet == 2) v = Terminal(WithCard(cards, r), n - 1, (a2 && h2 <= 11) ? h2 + 10 : h2, 2);
                else v = Play(WithCard(cards, r), n - 1, h2, a2, split);
                ev += pr * v;
                xc[r] = cr;
            }
            return ev;
        }

        /// <summary>
        /// A split hand of the pair card a and its second card r (already out of xc and in cards), played as the
        /// simulator plays it: split aces get one card unless they may be hit, and doubling needs DAS and isn't allowed
        /// on split aces.
        /// </summary>
        double SplitHand(long cards, double n, int a, int r)
        {
            int h = a + r;
            bool ace = a == 1 || r == 1;
            bool soft = ace && h <= 11;
            int best = soft ? h + 10 : h;
            if (a == 1 && !R.HitSplitAces) return Terminal(cards, n, best, 1);
            if (best == 21) return Terminal(cards, n, 21, 1);
            byte f = xFlags[soft ? PlayTable.SoftCell(best, xUp) : PlayTable.HardCell(h, xUp)];
            if ((f & Flag.Double) != 0 && R.DAS && a != 1 && R.CanDoubleTotal(h, soft)) return Draw(cards, n, h, ace, 2, true);
            if ((f & Flag.Stand) != 0) return Terminal(cards, n, best, 1);
            return Draw(cards, n, h, ace, 1, true);
        }

        /// <summary>Total value of the split hands still to come: pending hands waiting for their second card, of hands in all.</summary>
        double SplitDP(int pending, int hands)
        {
            if (pending == 0) return 0;
            int j = hands - 2;
            double pp = splitP[j];
            double v = (1 - pp) * (splitX[j] + SplitDP(pending - 1, hands));
            if (hands < sMaxHands) v += pp * SplitDP(pending + 1, hands + 1);
            else v += pp * (splitY[j] + SplitDP(pending - 1, hands));
            return v;
        }

        /// <summary>
        /// Expectation of splitting a pair of a, played as the simulator plays it (xc holds the cards left after the deal).
        /// Every split hand is played exactly with its own cards, the pair cards and the pair cards of earlier resplits
        /// out of the shoe. The cards of the other split hands stay in: in shuffle-every-round simulations of 1 to 8 decks
        /// that makes no measurable difference (0.002% or less), where the analysis's fixed draw odds were up to 0.02%
        /// too low with one deck.
        /// </summary>
        double SplitExact(int a, double Np, double pBJ, bool peekCond)
        {
            int maxHands = Math.Min(R.MaxSplitHands, Rules.MaxSplitLimit);
            if (a == 1 && !R.ResplitAces) maxHands = 2;
            sRank = a;
            sMaxHands = maxHands;
            long cards = WithCard(WithCard(0, a), a);
            double n = Np;
            int taken = 0;
            for (int j = 0; j <= maxHands - 2; j++)
            {
                if (j > 0)
                {
                    // Every resplit puts one more pair card on the table.
                    if (xc[a] < 1)
                    {
                        // No pair card left to resplit with: this many resplits can't happen.
                        splitP[j] = 0; splitX[j] = 0; splitY[j] = 0;
                        continue;
                    }
                    xc[a] -= 1;
                    cards = WithCard(cards, a);
                    n -= 1;
                    taken++;
                }
                sLevel = j;
                double na = xc[a];
                double pp = na / n;
                double sum = 0;
                for (int r = 1; r <= 10; r++)
                {
                    double cr = xc[r];
                    if (r == a || cr <= 0) continue;
                    xc[r] = cr - 1;
                    sum += cr / n * SplitHand(WithCard(cards, r), n - 1, a, r);
                    xc[r] = cr;
                }
                splitP[j] = pp;
                splitX[j] = pp < 1 ? sum / (1 - pp) : 0;
                splitY[j] = 0;
                if (j == maxHands - 2 && na > 0)
                {
                    // At the most hands a further pair card is played as it is.
                    xc[a] = na - 1;
                    splitY[j] = SplitHand(WithCard(cards, a), n - 1, a, a);
                    xc[a] = na;
                }
            }
            xc[a] += taken;
            double ev = SplitDP(2, 2);
            if (peekCond) ev -= pBJ;
            return ev;
        }

        /// <summary>Sets up the exact evaluation of a strategy (its flags) for these rules: dealer cache, game key, fresh memos.</summary>
        void BeginExact(byte[] flags)
        {
            if (dealerCache == null) dealerCache = new Dictionary<long, double[]>();
            xGame = R.Decks * 2 + (R.H17 ? 1 : 0);
            handMemo.Clear();
            splitMemo.Clear();
            xFlags = flags;
            xTakesAll = R.DealerBJTakesAll;
        }

        /// <summary>
        /// Off-the-top expectation of the strategy in basic (its flags and insurance) with every card the player draws
        /// taken out of the shoe: hits, doubles and split hands follow the flags exactly as the simulator plays them
        /// (splits as in SplitExact). Evaluate's PlayerEV assumes the best play after each hit, with the draw odds fixed
        /// after the first three cards, which is off by up to about 0.05% with one deck. In peek games each deal loses its
        /// blackjack probability once and the dealer's blackjacks are left out of every later outcome, which is exact
        /// because the order of the unseen cards doesn't matter.
        /// </summary>
        public double ExactOffTop(CAResult basic)
        {
            return ExactOn(FullShoe(R.Decks), basic);
        }

        // Effects of removal of recently used rules, per thread (they take ten exact evaluations).
        [ThreadStatic] static Dictionary<string, double[]> eorCache;
        [ThreadStatic] static Queue<string> eorOrder;

        /// <summary>
        /// The change in the player's expectation when one card of a rank is taken out of the full shoe ([1..10]), for
        /// basic strategy played unchanged and worked out exactly (ExactOffTop on each shoe). These are the effects of
        /// removal a count's betting correlation is defined with. Remembered per thread for the last few rule sets.
        /// </summary>
        public static double[] EffectsOfRemoval(Rules rules)
        {
            string key = BasicKey(rules);
            if (eorCache == null) { eorCache = new Dictionary<string, double[]>(); eorOrder = new Queue<string>(); }
            double[] eor;
            if (eorCache.TryGetValue(key, out eor)) return eor;
            ExactAdjust adj;
            double offTop;
            CAResult basic = BasicFor(rules, true, out adj, out offTop);
            double[] values = new double[11];
            var full = FullShoe(rules.Decks);
            // The ten shoes have nothing in common: each gets an engine of its own, on as many cores as there are.
            System.Threading.Tasks.Parallel.For(1, 11, k =>
            {
                var eng = new CAEngine(rules);
                eng.ownDealer = new Dictionary<long, double[]>();
                var shoe = (double[])full.Clone();
                shoe[k] -= 1;
                values[k] = eng.ExactOn(shoe, basic) - offTop;
            });
            eor = values;
            if (eorCache.Count >= 8) eorCache.Remove(eorOrder.Dequeue());
            eorCache[key] = eor;
            eorOrder.Enqueue(key);
            return eor;
        }

        /// <summary>ExactOffTop for a round dealt from shoe (the full shoe, or one with a card taken out).</summary>
        double ExactOn(double[] shoe, CAResult basic)
        {
            BeginExact(basic.Flags);
            bool surrAllowed = R.Surrender != SurrenderRule.None;
            bool srBeforePeek = R.SurrenderBeforePeek;
            bool peekCond = !R.DealerBJTakesAll;
            double N = Sum(shoe);
            double evTotal = 0;
            for (int u = 1; u <= 10; u++)
            {
                double pu = shoe[u] / N;
                if (pu <= 0) continue;
                xUp = u;
                for (int r = 1; r <= 10; r++) pop[r] = shoe[r];
                pop[u] = Math.Max(0, pop[u] - 1);
                double Npop = N - 1;
                for (int a = 1; a <= 10; a++)
                {
                    double wa = pop[a] / Npop;
                    if (wa <= 0) continue;
                    for (int b = a; b <= 10; b++)
                    {
                        double sb = pop[b] - (a == b ? 1 : 0);
                        if (sb <= 0) continue;
                        double w = wa * sb / (Npop - 1) * (a == b ? 1 : 2);
                        for (int r = 1; r <= 10; r++) xc[r] = pop[r];
                        xc[a] = Math.Max(0, xc[a] - 1);
                        xc[b] = Math.Max(0, xc[b] - 1);
                        double Np = Npop - 2;
                        double pBJ = u == 1 ? xc[10] / Np : (u == 10 ? xc[1] / Np : 0);
                        if (a == 1 && b == 10)
                        {
                            evTotal += pu * w * (1 - pBJ) * R.BlackjackPays;
                            continue;
                        }
                        int h0 = a + b;
                        bool ace0 = a == 1 || b == 1;
                        bool soft0 = ace0 && h0 + 10 <= 21;
                        byte pf = a == b ? xFlags[PlayTable.PairCell(a, u)] : (byte)0;
                        byte tf = xFlags[soft0 ? PlayTable.SoftCell(h0 + 10, u) : PlayTable.HardCell(h0, u)];
                        double evR = srBeforePeek ? -0.5 : -pBJ - 0.5 * (1 - pBJ);
                        double ev;
                        if (surrAllowed && (pf & Flag.Surrender) != 0) ev = evR;
                        else if ((pf & Flag.Split) != 0) ev = SplitExact(a, Np, pBJ, peekCond);
                        else if (surrAllowed && (tf & Flag.Surrender) != 0) ev = evR;
                        else
                        {
                            long cards = WithCard(WithCard(0, a), b);
                            if ((tf & Flag.Double) != 0 && R.CanDoubleTotal(h0, soft0)) ev = Draw(cards, Np, h0, ace0, 2, false);
                            else if ((tf & Flag.Stand) != 0) ev = Terminal(cards, Np, soft0 ? h0 + 10 : h0, 1);
                            else ev = Draw(cards, Np, h0, ace0, 1, false);
                            if (peekCond) ev -= pBJ;
                        }
                        evTotal += pu * w * ev;
                    }
                }
            }
            if (basic.Insure) evTotal += (shoe[1] / N) * basic.InsuranceEV;
            return evTotal;
        }
    }
}
