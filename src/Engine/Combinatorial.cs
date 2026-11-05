using System;
using System.Collections.Generic;

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
        /// <summary>Average EV of each option per cell: [cell*5 + k], k = stand, hit, double, surrender, split. NaN where not applicable.</summary>
        public double[] CellEV;
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
        readonly double[] pairAcc = new double[PlayTable.CellsPerBin * 2];

        struct Combo
        {
            public int U, A, B;
            public double W, S, Hh, D, Rr, P;
            public bool CanD;
        }
        readonly Combo[] combos = new Combo[600];
        int comboCount;

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
            int maxHands = Math.Min(R.MaxSplitHands, 4);
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
            return Evaluate(shoe, null);
        }

        /// <summary>
        /// Analysis of a round dealt from shoe. With a count model, shoe is the population before the deal and the cards
        /// each deal leaves unseen come from the model (the index plays at that count); deals are weighted by the shoe.
        /// </summary>
        public CAResult Evaluate(double[] shoe, CountModel model)
        {
            const int CPB = PlayTable.CellsPerBin;
            var flags = new byte[CPB];
            var cellEV = new double[CPB * 5];
            for (int i = 0; i < cellEV.Length; i++) cellEV[i] = double.NaN;
            Array.Clear(acc, 0, acc.Length);
            Array.Clear(accW, 0, accW.Length);
            Array.Clear(pairAcc, 0, pairAcc.Length);
            comboCount = 0;

            bool surrAllowed = R.Surrender != SurrenderRule.None;
            bool srBeforePeek = R.SurrenderBeforePeek;
            bool peekCond = !R.DealerBJTakesAll;
            bool canSplit = R.MaxSplitHands >= 2;
            double N = Sum(shoe);
            double evTotal = 0;
            var res = new CAResult();

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
                for (int r = 1; r <= 10; r++) p[r] = su[r] / Nu;
                FillDP();
                for (int t = 4; t <= 21; t++)
                {
                    flags[PlayTable.HardCell(t, u)] = sev[t] >= H[t * 2] ? Flag.Stand : (byte)0;
                    if (t >= 12)
                        flags[PlayTable.SoftCell(t, u)] = sev[t] >= H[(t - 10) * 2 + 1] ? Flag.Stand : (byte)0;
                }

                if (u == 1)
                {
                    double pT = su[10] / Nu;
                    res.InsuranceEV = 1.5 * pT - 0.5;
                    res.Insure = R.Insurance && pT > 1.0 / 3.0;
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
                            evTotal += pu * w * (1 - pBJ) * R.BlackjackPays;
                            continue;
                        }

                        dags[u].Evaluate(sp, Np, mass, dist);
                        double dBJ = 0;
                        if (peekCond) { double nb = 1 - dist[6]; if (nb > 1e-12) for (int k = 0; k < 6; k++) dist[k] /= nb; }
                        else dBJ = dist[6];
                        FillStand(dBJ);
                        for (int r = 1; r <= 10; r++) p[r] = sp[r] / Np;
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

                        int cell = soft0 ? PlayTable.SoftCell(best0, u) : PlayTable.HardCell(h0, u);
                        acc[cell * 4] += w * evS;
                        acc[cell * 4 + 1] += w * evH;
                        if (canD) acc[cell * 4 + 2] += w * evD;
                        if (surrAllowed) acc[cell * 4 + 3] += w * evR;
                        accW[cell] += w;

                        var c = new Combo();
                        c.U = u; c.A = a; c.B = b; c.W = w * pu;
                        c.S = evS; c.Hh = evH; c.D = evD; c.Rr = evR; c.P = evP; c.CanD = canD;
                        combos[comboCount++] = c;
                    }
                }
            }

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
                byte f = 0;
                if (S >= Hv) f |= Flag.Stand;
                double bestHS = Math.Max(S, Hv);
                if (D > bestHS) f |= Flag.Double;
                if (Rv > Math.Max(bestHS, D)) f |= Flag.Surrender;
                flags[cell] = f;
                cellEV[cell * 5] = S; cellEV[cell * 5 + 1] = Hv;
                if (canD) cellEV[cell * 5 + 2] = D;
                if (surrAllowed) cellEV[cell * 5 + 3] = Rv;
            }

            // Pass 2: pairs (compare splitting with how the total would otherwise be played).
            for (int i = 0; i < comboCount; i++)
            {
                Combo c = combos[i];
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
                cellEV[pcell * 5] = c.S;
                cellEV[pcell * 5 + 1] = c.Hh;
                if (c.CanD) cellEV[pcell * 5 + 2] = c.D;
                if (surrAllowed) cellEV[pcell * 5 + 3] = c.Rr;
                cellEV[pcell * 5 + 4] = canSplit ? c.P : double.NaN;
            }

            // Pass 3: expectation of the resulting strategy.
            for (int i = 0; i < comboCount; i++)
            {
                Combo c = combos[i];
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
            if (res.Insure) evTotal += (shoe[1] / N) * res.InsuranceEV;

            res.Flags = flags;
            res.PlayerEV = model == null ? evTotal : double.NaN;
            res.CellEV = cellEV;
            return res;
        }

        /// <summary>Off-the-top player expectation for the rules with optimal total-dependent basic strategy, played as the
        /// simulator plays it (see ExactOffTop).</summary>
        public static double HouseEdgeOffTop(Rules rules)
        {
            var e = new CAEngine(rules);
            return e.ExactOffTop(e.Evaluate(FullShoe(rules.Decks)));
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

        double[] RawDealer(long cards, double n)
        {
            long key = ((cards * 16 + xUp) << 5) + xGame;
            double[] d;
            if (dealerCache.TryGetValue(key, out d)) return d;
            dags[xUp].Evaluate(xc, n, mass, dist);
            d = (double[])dist.Clone();
            if (dealerCache.Count >= DealerCacheLimit) dealerCache.Clear();
            dealerCache[key] = d;
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
            int maxHands = Math.Min(R.MaxSplitHands, 4);
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

        /// <summary>
        /// Off-the-top expectation of the strategy in basic (its flags and insurance) with every card the player draws
        /// taken out of the shoe: hits, doubles and split hands follow the flags exactly as the simulator plays them
        /// (splits as in SplitExact). Evaluate's PlayerEV assumes the best play after each hit, with the draw odds fixed
        /// after the first three cards, which is up to 0.04% too high with one deck. In peek games each deal loses its
        /// blackjack probability once and the dealer's blackjacks are left out of every later outcome, which is exact
        /// because the order of the unseen cards doesn't matter.
        /// </summary>
        public double ExactOffTop(CAResult basic)
        {
            var shoe = FullShoe(R.Decks);
            if (dealerCache == null) dealerCache = new Dictionary<long, double[]>();
            xGame = R.Decks * 2 + (R.H17 ? 1 : 0);
            handMemo.Clear();
            splitMemo.Clear();
            xFlags = basic.Flags;
            xTakesAll = R.DealerBJTakesAll;
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
