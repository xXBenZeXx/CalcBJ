using System;

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
        /// <summary>Player expectation per initial unit for a round dealt from this composition (negative = house edge).</summary>
        public double PlayerEV;
        /// <summary>Insurance EV per unit of original bet when the dealer shows an ace.</summary>
        public double InsuranceEV;
        /// <summary>Average EV of each option per cell: [cell*5 + k], k = stand, hit, double, surrender, split. NaN where not applicable.</summary>
        public double[] CellEV;
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

        public CAResult Evaluate(double[] shoe)
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
                for (int r = 1; r <= 10; r++) su[r] = shoe[r];
                su[u] = Math.Max(0, su[u] - 1);
                double Nu = N - 1;

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
                    double wa = su[a] / Nu;
                    if (wa <= 0) continue;
                    for (int b = a; b <= 10; b++)
                    {
                        double sb = su[b] - (a == b ? 1 : 0);
                        if (sb <= 0) continue;
                        double w = wa * sb / (Nu - 1) * (a == b ? 1 : 2);
                        for (int r = 1; r <= 10; r++) sp[r] = su[r];
                        sp[a] = Math.Max(0, sp[a] - 1);
                        sp[b] = Math.Max(0, sp[b] - 1);
                        double Np = Nu - 2;
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
                cellEV[pcell * 5 + 4] = canSplit ? c.P : double.NaN;
                cellEV[pcell * 5] = nonSplit;
                if (surrAllowed) cellEV[pcell * 5 + 3] = c.Rr;
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
            res.PlayerEV = evTotal;
            res.CellEV = cellEV;
            return res;
        }

        /// <summary>Off-the-top player expectation for the rules with optimal total-dependent basic strategy.</summary>
        public static double HouseEdgeOffTop(Rules rules)
        {
            var e = new CAEngine(rules);
            return e.Evaluate(FullShoe(rules.Decks)).PlayerEV;
        }
    }
}
