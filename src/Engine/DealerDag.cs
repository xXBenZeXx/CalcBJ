using System;
using System.Collections.Generic;

namespace CalcBJ.Engine
{
    /// <summary>
    /// All multisets of cards the dealer can draw behind a given upcard, in breadth-first order.
    /// Because the stopping rule and the draw probabilities depend only on which cards are out
    /// (not their order), evaluating this DAG gives the exact dealer distribution with card removal.
    /// </summary>
    internal sealed class DealerDag
    {
        public int Count;
        public byte[] M;        // Count*10 : number of each rank drawn (index rank-1)
        public int[] K;         // cards drawn (hole card included)
        public sbyte[] Outcome; // -1 = draws again; 0..4 = 17..21; 5 = bust; 6 = blackjack
        public int[] Child;     // Count*10

        public const int Bust = 5;
        public const int Blackjack = 6;

        static readonly DealerDag[,] cache = new DealerDag[11, 2];
        static readonly object cacheLock = new object();

        public static DealerDag Get(int up, bool h17)
        {
            int hi = h17 ? 1 : 0;
            lock (cacheLock)
            {
                if (cache[up, hi] == null) cache[up, hi] = Build(up, h17);
                return cache[up, hi];
            }
        }

        static DealerDag Build(int up, bool h17)
        {
            var index = new Dictionary<long, int>();
            var keys = new List<long>();
            var counts = new List<byte[]>();
            index[0L] = 0; keys.Add(0L); counts.Add(new byte[10]);
            var outcome = new List<sbyte>();
            var child = new List<int>();

            for (int i = 0; i < keys.Count; i++)
            {
                byte[] m = counts[i];
                int k = 0, h = up;
                for (int r = 0; r < 10; r++) { k += m[r]; h += m[r] * (r + 1); }
                bool ace = up == 1 || m[0] > 0;
                sbyte o = -1;
                if (k >= 1)
                {
                    if (k == 1 && ((up == 1 && m[9] == 1) || (up == 10 && m[0] == 1))) o = Blackjack;
                    else if (h > 21) o = Bust;
                    else
                    {
                        int best = (ace && h + 10 <= 21) ? h + 10 : h;
                        bool hitSoft17 = h17 && best == 17 && ace && h + 10 == 17;
                        if (best >= 17 && !hitSoft17) o = (sbyte)(best - 17);
                    }
                }
                outcome.Add(o);
                for (int r = 0; r < 10; r++)
                {
                    if (o >= 0) { child.Add(-1); continue; }
                    long ck = keys[i] + (1L << (4 * r));
                    int ci;
                    if (!index.TryGetValue(ck, out ci))
                    {
                        ci = keys.Count;
                        index[ck] = ci;
                        keys.Add(ck);
                        var cm = (byte[])m.Clone();
                        cm[r]++;
                        counts.Add(cm);
                    }
                    child.Add(ci);
                }
            }

            var dag = new DealerDag();
            dag.Count = keys.Count;
            dag.M = new byte[dag.Count * 10];
            dag.K = new int[dag.Count];
            dag.Outcome = outcome.ToArray();
            dag.Child = child.ToArray();
            for (int i = 0; i < dag.Count; i++)
            {
                int k = 0;
                for (int r = 0; r < 10; r++) { dag.M[i * 10 + r] = counts[i][r]; k += counts[i][r]; }
                dag.K[i] = k;
            }
            return dag;
        }

        /// <summary>
        /// Dealer final-total distribution. comp[1..10] = cards of each rank still available to the dealer, n = their sum.
        /// dist[0..4] = P(17..21), dist[5] = P(bust), dist[6] = P(blackjack).
        /// </summary>
        public void Evaluate(double[] comp, double n, double[] mass, double[] dist)
        {
            Array.Clear(mass, 0, Count);
            for (int i = 0; i < 7; i++) dist[i] = 0;
            mass[0] = 1.0;
            byte[] mm = M; int[] kk = K; sbyte[] oo = Outcome; int[] ch = Child;
            for (int s = 0; s < Count; s++)
            {
                double ms = mass[s];
                if (ms == 0) continue;
                int o = oo[s];
                if (o >= 0) { dist[o] += ms; continue; }
                double denom = n - kk[s];
                if (denom <= 1e-12) continue;
                double f = ms / denom;
                int b = s * 10;
                for (int r = 0; r < 10; r++)
                {
                    double avail = comp[r + 1] - mm[b + r];
                    if (avail > 1e-12) mass[ch[b + r]] += f * avail;
                }
            }
        }
    }
}
