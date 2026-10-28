using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading.Tasks;

namespace CalcBJ.Engine
{
    /// <summary>
    /// Decision flags for every count bin. Cell layout per bin:
    /// hard totals [0..21]x10 upcards, soft totals [0..21]x10, pairs [A..T]x10.
    /// </summary>
    public sealed class PlayTable
    {
        public const int CellsPerBin = 540;
        public const int BinMin = -100;
        public const int BinMax = 100;
        public const int NumBins = BinMax - BinMin + 1;

        public static int HardCell(int total, int up) { return total * 10 + (up - 1); }
        public static int SoftCell(int total, int up) { return 220 + total * 10 + (up - 1); }
        public static int PairCell(int rank, int up) { return 440 + (rank - 1) * 10 + (up - 1); }

        public byte[] Flags;
        public bool[] Insure;
        public byte[] Basic;
        public CAResult BasicResult;
        public double[] BinTcEq;
        public double[] BinEV;
        public string Key;
        /// <summary>CA results for the count values inside the index range (null for basic strategy only).</summary>
        public CAResult[] PerBin;
        public int PerBinMin;

        public CAResult ResultAt(int binValue)
        {
            if (PerBin == null || PerBin.Length == 0) return BasicResult;
            int i = Math.Max(0, Math.Min(PerBin.Length - 1, binValue - PerBinMin));
            return PerBin[i];
        }

        public byte FlagsAt(int binValue, int cell)
        {
            int b = Math.Max(BinMin, Math.Min(BinMax, binValue)) - BinMin;
            return Flags[b * CellsPerBin + cell];
        }

        public bool InsureAt(int binValue)
        {
            int b = Math.Max(BinMin, Math.Min(BinMax, binValue)) - BinMin;
            return Insure[b];
        }
    }

    public sealed class IndexInfo
    {
        public HandKind Kind;
        public int Total;
        public int Upcard;
        public string Hand;
        public string Play;
        public string Condition;
        public double Threshold;
        public bool AtOrAbove;
        public bool Irregular;
        public string Text { get { return Play + " " + Condition; } }
    }

    public static class StrategyBuilder
    {
        struct CellMask
        {
            public int Cell; public byte Mask;
            public CellMask(int cell, byte mask) { Cell = cell; Mask = mask; }
        }

        static readonly CellMask[] I18 =
        {
            new CellMask(PlayTable.HardCell(16, 10), Flag.Stand),
            new CellMask(PlayTable.HardCell(15, 10), Flag.Stand),
            new CellMask(PlayTable.PairCell(10, 5), Flag.Split),
            new CellMask(PlayTable.PairCell(10, 6), Flag.Split),
            new CellMask(PlayTable.HardCell(10, 10), Flag.Double),
            new CellMask(PlayTable.HardCell(12, 3), Flag.Stand),
            new CellMask(PlayTable.HardCell(12, 2), Flag.Stand),
            new CellMask(PlayTable.HardCell(11, 1), Flag.Double),
            new CellMask(PlayTable.HardCell(9, 2), Flag.Double),
            new CellMask(PlayTable.HardCell(10, 1), Flag.Double),
            new CellMask(PlayTable.HardCell(9, 7), Flag.Double),
            new CellMask(PlayTable.HardCell(16, 9), Flag.Stand),
            new CellMask(PlayTable.HardCell(13, 2), Flag.Stand),
            new CellMask(PlayTable.HardCell(12, 4), Flag.Stand),
            new CellMask(PlayTable.HardCell(12, 5), Flag.Stand),
            new CellMask(PlayTable.HardCell(12, 6), Flag.Stand),
            new CellMask(PlayTable.HardCell(13, 3), Flag.Stand),
        };

        static readonly CellMask[] Fab4 =
        {
            new CellMask(PlayTable.HardCell(14, 10), Flag.Surrender),
            new CellMask(PlayTable.HardCell(15, 10), Flag.Surrender),
            new CellMask(PlayTable.HardCell(15, 9), Flag.Surrender),
            new CellMask(PlayTable.HardCell(15, 1), Flag.Surrender),
        };

        /// <summary>Average decks remaining over the rounds dealt before the cut card.</summary>
        public static double MidDepthDecks(Rules r)
        {
            return r.Decks * (1 + (1 - r.Penetration)) / 2.0;
        }

        /// <summary>Typical exact count value inside an integer bin, given how the bin was rounded.</summary>
        public static double Representative(CountSystem cs, int v)
        {
            if (cs.Metric == CountMetric.RunningCount)
            {
                int s = cs.Scale();
                return v + (s - 1) / (2.0 * s);
            }
            switch (cs.Rounding)
            {
                case TcRounding.Floor: return v + 0.5;
                case TcRounding.Truncate: return v > 0 ? v + 0.5 : (v < 0 ? v - 0.5 : 0);
                default: return v;
            }
        }

        /// <summary>
        /// Where the decisions of each count bin fall in the shoe: the expected number of unseen cards (unseen) and the
        /// expected average tag of the unseen cards (unseenMean), for decisions spread evenly over the dealt part of the
        /// shoe, with the running count binned exactly as the simulator does. Running counts move in whole cards and true
        /// counts cluster towards zero, so a bin's midpoint is a poor stand-in for the counts in it, most of all with one
        /// or two decks. Bins that never occur get NaN.
        /// </summary>
        public static void BinMoments(Rules rules, CountSystem cs, double[] unseen, double[] unseenMean)
        {
            int N = rules.TotalCards;
            int scale = cs.Scale();
            int irc = (int)Math.Round(cs.Irc(rules.Decks) * scale);
            double mu = 0, m2 = 0;
            for (int code = 0; code < 20; code++)
            {
                int rank = (code >> 1) + 1;
                double share = (rank == 10 ? 8.0 : 2.0) / 52.0;
                double t = Math.Round(cs.TagOfCode(code) * scale);
                mu += share * t;
                m2 += share * t * t;
            }
            double var1 = m2 - mu * mu;
            double total = mu * N;
            var w = new double[PlayTable.NumBins];
            var wn = new double[PlayTable.NumBins];
            var wm = new double[PlayTable.NumBins];
            int last = Math.Max(3, Math.Min(N - 2, (int)Math.Round(N * rules.Penetration)));
            for (int k = 3; k <= last; k++)
            {
                // Seen cards' scaled tag sum: k cards drawn without replacement from the shoe.
                int n = N - k;
                double mean = k * mu;
                double sd = Math.Sqrt(Math.Max(0, k * var1 * n / (N - 1.0)));
                int s0 = (int)Math.Floor(mean - 6 * sd), s1 = (int)Math.Ceiling(mean + 6 * sd);
                double z = 0;
                for (int s = s0; s <= s1; s++) z += sd > 0 ? Math.Exp(-0.5 * (s - mean) * (s - mean) / (sd * sd)) : (s == s0 ? 1 : 0);
                if (z <= 0) continue;
                for (int s = s0; s <= s1; s++)
                {
                    double pr = (sd > 0 ? Math.Exp(-0.5 * (s - mean) * (s - mean) / (sd * sd)) : (s == s0 ? 1 : 0)) / z;
                    if (pr <= 0) continue;
                    // The hole card is dealt but unseen: the simulator's count uses the cards left after it.
                    int v = Simulator.MetricValue(cs, scale, irc + s, n - 1);
                    if (v < PlayTable.BinMin) v = PlayTable.BinMin; else if (v > PlayTable.BinMax) v = PlayTable.BinMax;
                    int b = v - PlayTable.BinMin;
                    w[b] += pr;
                    wn[b] += pr * n;
                    wm[b] += pr * (total - s) / scale / n;
                }
            }
            for (int b = 0; b < PlayTable.NumBins; b++)
            {
                unseen[b] = w[b] > 1e-12 ? wn[b] / w[b] : double.NaN;
                unseenMean[b] = w[b] > 1e-12 ? wm[b] / w[b] : double.NaN;
            }
        }

        /// <summary>Converts a betting/playing count value to the balanced-equivalent true count at mid depth.</summary>
        public static double TcEquivalent(CountSystem cs, Rules r, double metricValue)
        {
            double dMid = MidDepthDecks(r);
            double rc = cs.Metric == CountMetric.TrueCount ? metricValue * dMid : metricValue;
            return (rc - cs.Irc(r.Decks) - cs.ImbalancePerDeck() * (r.Decks - dMid)) / dMid;
        }

        /// <summary>
        /// Expected rank composition of the unseen cards when the (balanced-equivalent) true count equals tcEq,
        /// from the linear regression of composition on the count: q_r = p_r (1 - T (t_r - m) / K).
        /// </summary>
        public static double[] ShiftedShoe(CountSystem cs, double tcEq, double decksRemaining)
        {
            double m = cs.ImbalancePerDeck() / 52.0;
            double K = 0;
            for (int code = 0; code < 20; code++)
            {
                int rank = (code >> 1) + 1;
                double perDeck = rank == 10 ? 8 : 2;
                double d = cs.TagOfCode(code) - m;
                K += perDeck * d * d;
            }
            var comp = new double[11];
            double maxDev = 0;
            for (int r = 1; r <= 10; r++) maxDev = Math.Max(maxDev, Math.Abs(cs.RankTag(r) - m));
            double T = tcEq;
            if (K > 1e-12 && maxDev > 1e-12)
            {
                double lim = 0.9 * K / maxDev;
                if (T > lim) T = lim; else if (T < -lim) T = -lim;
            }
            for (int r = 1; r <= 10; r++)
            {
                double pr = (r == 10 ? 16.0 : 4.0) / 52.0;
                double f = K > 1e-12 ? 1 - T * (cs.RankTag(r) - m) / K : 1;
                if (f < 0.02) f = 0.02;
                comp[r] = pr * 52.0 * decksRemaining * f;
            }
            return comp;
        }

        public static string KeyOf(Rules r, CountSystem cs, StrategyConfig sc)
        {
            return r.Key() + "#" + cs.Key() + "#" + sc.Key();
        }

        public static PlayTable Build(Rules rules, CountSystem cs, StrategyConfig sc)
        {
            const int CPB = PlayTable.CellsPerBin;
            var pt = new PlayTable();
            pt.Key = KeyOf(rules, cs, sc);
            pt.Flags = new byte[PlayTable.NumBins * CPB];
            pt.Insure = new bool[PlayTable.NumBins];
            pt.BinTcEq = new double[PlayTable.NumBins];
            pt.BinEV = new double[PlayTable.NumBins];
            for (int i = 0; i < PlayTable.NumBins; i++) { pt.BinEV[i] = double.NaN; pt.BinTcEq[i] = double.NaN; }

            var basicEngine = new CAEngine(rules);
            pt.BasicResult = basicEngine.Evaluate(CAEngine.FullShoe(rules.Decks));
            pt.BasicResult.PlayerEV = basicEngine.ExactOffTop(pt.BasicResult);
            pt.Basic = pt.BasicResult.Flags;

            int lo = Math.Max(PlayTable.BinMin, Math.Min(sc.IndexMin, sc.IndexMax));
            int hi = Math.Min(PlayTable.BinMax, Math.Max(sc.IndexMin, sc.IndexMax));
            int span = hi - lo + 1;
            CAResult[] perBin = null;
            if (sc.Mode != IndexMode.BasicOnly)
            {
                perBin = new CAResult[span];
                double dMid = MidDepthDecks(rules);
                var full = CAEngine.FullShoe(rules.Decks);
                var tags = new double[11];
                for (int r = 1; r <= 10; r++) tags[r] = cs.RankTag(r);
                double totalTags = cs.ImbalancePerDeck() * rules.Decks, irc = cs.Irc(rules.Decks);
                var unseen = new double[PlayTable.NumBins];
                var unseenMean = new double[PlayTable.NumBins];
                BinMoments(rules, cs, unseen, unseenMean);
                Parallel.For(0, span, () => new CAEngine(rules), (i, state, eng) =>
                {
                    // The count when the player decides includes the upcard and their cards (see CountModel).
                    var model = new CountModel();
                    model.Tags = tags;
                    int b = lo + i - PlayTable.BinMin;
                    if (!double.IsNaN(unseen[b]))
                    {
                        model.Unseen = unseen[b];
                        model.UnseenTags = unseenMean[b] * unseen[b];
                    }
                    else
                    {
                        // A count this game never reaches: extend from the bin's representative value at mid depth.
                        double rep = Representative(cs, lo + i);
                        double rc = cs.Metric == CountMetric.TrueCount ? rep * dMid : rep;
                        model.Unseen = 52 * dMid;
                        model.UnseenTags = totalTags - (rc - irc);
                    }
                    perBin[i] = eng.Evaluate(full, model);
                    return eng;
                }, eng => { });
                pt.PerBin = perBin;
                pt.PerBinMin = lo;
            }

            for (int b = 0; b < PlayTable.NumBins; b++)
            {
                int v = PlayTable.BinMin + b;
                int vc = Math.Max(lo, Math.Min(hi, v));
                CAResult src = perBin == null ? null : perBin[vc - lo];
                if (src != null)
                {
                    pt.BinTcEq[b] = TcEquivalent(cs, rules, Representative(cs, vc));
                    pt.BinEV[b] = src.PlayerEV;
                }
                int off = b * CPB;
                switch (sc.Mode)
                {
                    case IndexMode.FullIndices:
                        Buffer.BlockCopy(src.Flags, 0, pt.Flags, off, CPB);
                        pt.Insure[b] = src.Insure;
                        break;
                    case IndexMode.Illustrious18:
                    case IndexMode.I18Fab4:
                        Buffer.BlockCopy(pt.Basic, 0, pt.Flags, off, CPB);
                        foreach (var cm in I18) Merge(pt.Flags, off + cm.Cell, src.Flags[cm.Cell], cm.Mask);
                        if (sc.Mode == IndexMode.I18Fab4 && rules.Surrender != SurrenderRule.None)
                            foreach (var cm in Fab4) Merge(pt.Flags, off + cm.Cell, src.Flags[cm.Cell], cm.Mask);
                        pt.Insure[b] = src.Insure;
                        break;
                    default:
                        Buffer.BlockCopy(pt.Basic, 0, pt.Flags, off, CPB);
                        pt.Insure[b] = false;
                        break;
                }
            }

            if (sc.Overrides != null)
                foreach (var o in sc.Overrides) ApplyOverride(pt, o, rules);
            return pt;
        }

        static void Merge(byte[] flags, int idx, byte src, byte mask)
        {
            flags[idx] = (byte)((flags[idx] & ~mask) | (src & mask));
        }

        static void ApplyOverride(PlayTable pt, IndexOverride o, Rules rules)
        {
            if (!o.Enabled) return;
            bool positive = o.Play == PlayKind.Stand || o.Play == PlayKind.Double || o.Play == PlayKind.Split
                || o.Play == PlayKind.Surrender || o.Play == PlayKind.Insure;
            for (int b = 0; b < PlayTable.NumBins; b++)
            {
                int v = PlayTable.BinMin + b;
                bool cond = o.AtOrAbove ? v >= o.Index - 1e-9 : v <= o.Index + 1e-9;
                bool val = positive ? cond : !cond;
                if (o.Kind == HandKind.Insurance || o.Play == PlayKind.Insure || o.Play == PlayKind.NoInsure)
                {
                    pt.Insure[b] = val && rules.Insurance;
                    continue;
                }
                if (o.Upcard < 1 || o.Upcard > 10) return;
                int cell;
                if (o.Kind == HandKind.Pair) { if (o.Total < 1 || o.Total > 10) return; cell = PlayTable.PairCell(o.Total, o.Upcard); }
                else if (o.Kind == HandKind.Soft) { if (o.Total < 12 || o.Total > 21) return; cell = PlayTable.SoftCell(o.Total, o.Upcard); }
                else { if (o.Total < 4 || o.Total > 21) return; cell = PlayTable.HardCell(o.Total, o.Upcard); }
                byte mask;
                switch (o.Play)
                {
                    case PlayKind.Stand: case PlayKind.Hit: mask = Flag.Stand; break;
                    case PlayKind.Double: case PlayKind.NoDouble: mask = Flag.Double; break;
                    case PlayKind.Split: case PlayKind.NoSplit: mask = Flag.Split; break;
                    default: mask = Flag.Surrender; break;
                }
                int idx = b * PlayTable.CellsPerBin + cell;
                if (val) pt.Flags[idx] |= mask; else pt.Flags[idx] = (byte)(pt.Flags[idx] & ~mask);
            }
        }

        /// <summary>Chart code for a cell's flags: H, S, D, Ds, P, R, Rs, Rp; pairs without split return "".</summary>
        public static string ActionCode(byte f, bool pairCell)
        {
            if (pairCell)
            {
                if ((f & Flag.Surrender) != 0) return (f & Flag.Split) != 0 ? "Rp" : "R";
                return (f & Flag.Split) != 0 ? "P" : "";
            }
            if ((f & Flag.Surrender) != 0) return (f & Flag.Stand) != 0 ? "Rs" : "R";
            if ((f & Flag.Double) != 0) return (f & Flag.Stand) != 0 ? "Ds" : "D";
            return (f & Flag.Stand) != 0 ? "S" : "H";
        }

        static string PlayName(byte mask, bool on)
        {
            switch (mask)
            {
                case Flag.Stand: return on ? "Stand" : "Hit";
                case Flag.Double: return on ? "Double" : "No double";
                case Flag.Split: return on ? "Split" : "No split";
                default: return on ? "Surrender" : "No surrender";
            }
        }

        public static string HandName(HandKind kind, int total)
        {
            switch (kind)
            {
                case HandKind.Soft: return "A," + (total - 11 == 1 ? "A" : (total - 11).ToString(CultureInfo.InvariantCulture));
                case HandKind.Pair: { string r = Cards.RankName(total); return r + "," + r; }
                case HandKind.Insurance: return "Insurance";
                default: return total.ToString(CultureInfo.InvariantCulture);
            }
        }

        static string Signed(int v) { return v > 0 ? "+" + v.ToString(CultureInfo.InvariantCulture) : v.ToString(CultureInfo.InvariantCulture); }

        static void Describe(List<IndexInfo> list, PlayTable pt, HandKind kind, int total, int up, int cell, byte mask, int lo, int hi)
        {
            bool basic = (pt.Basic[cell] & mask) != 0;
            int minD = int.MaxValue, maxD = int.MinValue, count = 0;
            for (int v = lo; v <= hi; v++)
            {
                bool f = (pt.FlagsAt(v, cell) & mask) != 0;
                if (f != basic) { count++; if (v < minD) minD = v; if (v > maxD) maxD = v; }
            }
            if (count == 0) return;
            var info = new IndexInfo();
            info.Kind = kind; info.Total = total; info.Upcard = up;
            info.Hand = HandName(kind, total) + " v " + Cards.RankName(up);
            info.Play = PlayName(mask, !basic);
            info.Irregular = count != maxD - minD + 1 || (minD != lo && maxD != hi);
            if (maxD == hi && (minD != lo || minD > 0))
            {
                info.AtOrAbove = true; info.Threshold = minD; info.Condition = "at " + Signed(minD) + " or more";
            }
            else if (minD == lo && maxD != hi)
            {
                info.AtOrAbove = false; info.Threshold = maxD; info.Condition = "at " + Signed(maxD) + " or less";
            }
            else if (minD == lo && maxD == hi && count == hi - lo + 1)
            {
                info.AtOrAbove = true; info.Threshold = lo; info.Condition = "always";
            }
            else if (minD == lo && maxD == hi)
            {
                // Deviates at both ends of the range: name the two ends, e.g. "at -4 or less, and at +10 or more".
                int lowEnd = lo, highStart = hi;
                while (lowEnd + 1 <= hi && (pt.FlagsAt(lowEnd + 1, cell) & mask) != 0 != basic) lowEnd++;
                while (highStart - 1 >= lo && (pt.FlagsAt(highStart - 1, cell) & mask) != 0 != basic) highStart--;
                info.AtOrAbove = false; info.Threshold = lowEnd;
                info.Condition = "at " + Signed(lowEnd) + " or less, and at " + Signed(highStart) + " or more";
            }
            else
            {
                info.AtOrAbove = true; info.Threshold = minD; info.Condition = "from " + Signed(minD) + " to " + Signed(maxD);
            }
            list.Add(info);
        }

        /// <summary>Lists every decision that differs from basic strategy somewhere in [lo, hi].</summary>
        public static List<IndexInfo> DescribeIndices(PlayTable pt, int lo, int hi)
        {
            var list = new List<IndexInfo>();
            int[] ups = Cards.ChartUpOrder;
            // Insurance
            {
                int minD = int.MaxValue, count = 0;
                for (int v = lo; v <= hi; v++) if (pt.InsureAt(v)) { count++; if (v < minD) minD = v; }
                if (count > 0)
                {
                    var info = new IndexInfo();
                    info.Kind = HandKind.Insurance; info.Upcard = 1; info.Hand = "Insurance"; info.Play = "Insure";
                    info.AtOrAbove = true; info.Threshold = minD; info.Condition = "at " + Signed(minD) + " or more";
                    list.Add(info);
                }
            }
            for (int t = 20; t >= 4; t--)
                foreach (int u in ups)
                {
                    int cell = PlayTable.HardCell(t, u);
                    Describe(list, pt, HandKind.Hard, t, u, cell, Flag.Stand, lo, hi);
                    Describe(list, pt, HandKind.Hard, t, u, cell, Flag.Double, lo, hi);
                    Describe(list, pt, HandKind.Hard, t, u, cell, Flag.Surrender, lo, hi);
                }
            for (int t = 20; t >= 13; t--)
                foreach (int u in ups)
                {
                    int cell = PlayTable.SoftCell(t, u);
                    Describe(list, pt, HandKind.Soft, t, u, cell, Flag.Stand, lo, hi);
                    Describe(list, pt, HandKind.Soft, t, u, cell, Flag.Double, lo, hi);
                    Describe(list, pt, HandKind.Soft, t, u, cell, Flag.Surrender, lo, hi);
                }
            for (int r = 1; r <= 10; r++)
                foreach (int u in ups)
                {
                    int cell = PlayTable.PairCell(r, u);
                    Describe(list, pt, HandKind.Pair, r, u, cell, Flag.Split, lo, hi);
                    Describe(list, pt, HandKind.Pair, r, u, cell, Flag.Surrender, lo, hi);
                }
            return list;
        }
    }
}
