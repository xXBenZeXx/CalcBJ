using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;

namespace CalcBJ.Engine
{
    /// <summary>Per-count-bin results of a simulation. Results are stored in twentieths of a unit so they stay exact.</summary>
    public sealed class SimData
    {
        public int BinMin;
        public int NumBins;
        public int Spots;
        public long[] N, SA, QA, SB, QB, AB;
        public long Rounds, Shoes, Emergency;
        public double Seconds;
        public int Threads;
        public ulong Seed;
        public string Key;
        public string RulesText;
        public string CountText;
        public string StrategyText;
        public CountMetric Metric;
        public double BasicEV;
        public DateTime Created;

        public static SimData Create(int spots)
        {
            var d = new SimData();
            d.BinMin = PlayTable.BinMin;
            d.NumBins = PlayTable.NumBins;
            d.Spots = spots;
            d.N = new long[d.NumBins]; d.SA = new long[d.NumBins]; d.QA = new long[d.NumBins];
            d.SB = new long[d.NumBins]; d.QB = new long[d.NumBins]; d.AB = new long[d.NumBins];
            d.Created = DateTime.Now;
            return d;
        }

        public SimData Clone()
        {
            var c = (SimData)MemberwiseClone();
            c.N = (long[])N.Clone(); c.SA = (long[])SA.Clone(); c.QA = (long[])QA.Clone();
            c.SB = (long[])SB.Clone(); c.QB = (long[])QB.Clone(); c.AB = (long[])AB.Clone();
            return c;
        }

        /// <summary>Combines two independent runs of the same configuration (their counts simply add up).</summary>
        public static SimData Merge(SimData a, SimData b)
        {
            if (a == null) return b == null ? null : b.Clone();
            if (b == null) return a.Clone();
            var m = b.Clone();
            for (int i = 0; i < m.NumBins && i < a.NumBins; i++)
            {
                m.N[i] += a.N[i]; m.SA[i] += a.SA[i]; m.QA[i] += a.QA[i];
                m.SB[i] += a.SB[i]; m.QB[i] += a.QB[i]; m.AB[i] += a.AB[i];
            }
            m.Rounds += a.Rounds; m.Shoes += a.Shoes; m.Emergency += a.Emergency; m.Seconds += a.Seconds;
            return m;
        }

        internal void AddFrom(SimWorker w)
        {
            for (int i = 0; i < NumBins; i++)
            {
                N[i] += w.N[i]; SA[i] += w.SA[i]; QA[i] += w.QA[i];
                SB[i] += w.SB[i]; QB[i] += w.QB[i]; AB[i] += w.AB[i];
            }
            Rounds += w.Rounds; Shoes += w.Shoes; Emergency += w.Emergency;
        }
    }

    public sealed class SimSetup
    {
        public Rules Rules;
        public CountSystem Count;
        public PlayTable Table;
        public int Spots;
        public long Rounds;
        public int Threads;
        public ulong Seed;
    }

    /// <summary>
    /// Count-bin lookup by (cards remaining, running count). Cards-remaining values that share a deck estimate share a
    /// row, so for ¼/½/full-deck estimation or running-count play the table is only a few KB and stays in cache.
    /// </summary>
    internal sealed class BinLookup
    {
        public int RcMin, RcMax, Width;
        public int[] CrRow;
        public short[] Rows;
    }

    /// <summary>Runs a simulation on several threads; results can be read while it runs.</summary>
    public sealed class Simulator
    {
        public const long Chunk = 250000;
        readonly SimSetup setup;
        readonly BinLookup bins;
        readonly byte[] fixedFlags;
        readonly SimData acc;
        readonly object lk = new object();
        readonly Stopwatch sw = new Stopwatch();
        long claimed;
        long done;
        int running;
        volatile bool cancel;
        public Exception Error;
        public event EventHandler Finished;

        public Simulator(SimSetup s)
        {
            setup = s;
            bins = BuildBins(s.Rules, s.Count);
            fixedFlags = FixedCells(s.Table);
            acc = SimData.Create(s.Spots);
            acc.Threads = s.Threads;
            acc.Seed = s.Seed;
        }

        public SimSetup Setup { get { return setup; } }
        public long Target { get { return setup.Rounds; } }
        public long Done { get { return Interlocked.Read(ref done); } }
        public bool IsRunning { get { return Thread.VolatileRead(ref running) > 0; } }
        public bool Cancelled { get { return cancel; } }
        public double ElapsedSeconds { get { return sw.Elapsed.TotalSeconds; } }

        public void Start()
        {
            int n = Math.Max(1, setup.Threads);
            running = n;
            sw.Start();
            for (int i = 0; i < n; i++)
            {
                int idx = i;
                var t = new Thread(() => Work(idx));
                t.IsBackground = true;
                t.Priority = ThreadPriority.BelowNormal;
                t.Name = "sim-" + i;
                t.Start();
            }
        }

        public void Stop() { cancel = true; }

        void Work(int idx)
        {
            SimWorker w = null;
            try
            {
                w = new SimWorker(setup, bins, fixedFlags, setup.Seed + (ulong)(idx + 1) * 0x9E3779B97F4A7C15UL);
                w.Shuffle();
                while (!cancel)
                {
                    long start = Interlocked.Add(ref claimed, Chunk) - Chunk;
                    if (start >= setup.Rounds) break;
                    long n = Math.Min(Chunk, setup.Rounds - start);
                    w.Run(n);
                    lock (lk)
                    {
                        acc.AddFrom(w);
                        w.Reset();
                        done += n;
                    }
                }
            }
            catch (Exception ex)
            {
                Error = ex;
                cancel = true;
            }
            finally
            {
                if (w != null) w.Dispose();
                if (Interlocked.Decrement(ref running) == 0)
                {
                    sw.Stop();
                    var h = Finished;
                    if (h != null) h(this, EventArgs.Empty);
                }
            }
        }

        public SimData Snapshot()
        {
            lock (lk)
            {
                var s = acc.Clone();
                s.Seconds = sw.Elapsed.TotalSeconds;
                return s;
            }
        }

        public static double EstimateDecks(DeckEstimation est, int cardsRemaining)
        {
            switch (est)
            {
                case DeckEstimation.QuarterDeck: return Math.Max(1.0, Math.Round(cardsRemaining / 13.0, MidpointRounding.AwayFromZero)) / 4.0;
                case DeckEstimation.HalfDeck: return Math.Max(1.0, Math.Round(cardsRemaining / 26.0, MidpointRounding.AwayFromZero)) / 2.0;
                case DeckEstimation.FullDeck: return Math.Max(1.0, Math.Round(cardsRemaining / 52.0, MidpointRounding.AwayFromZero));
                default: return Math.Max(cardsRemaining, 6) / 52.0;
            }
        }

        /// <summary>Count value (TC or RC, in tag units) for a scaled running count and cards remaining.</summary>
        public static int MetricValue(CountSystem cs, int scale, int rcScaled, int cardsRemaining)
        {
            if (cs.Metric == CountMetric.RunningCount)
                return (int)Math.Floor((double)rcScaled / scale + 1e-9);
            double tc = rcScaled / (scale * EstimateDecks(cs.DeckEst, cardsRemaining));
            switch (cs.Rounding)
            {
                case TcRounding.Truncate: return (int)(tc + (tc >= 0 ? 1e-9 : -1e-9));
                case TcRounding.Round: return (int)Math.Round(tc, MidpointRounding.AwayFromZero);
                default: return (int)Math.Floor(tc + 1e-9);
            }
        }

        internal static BinLookup BuildBins(Rules r, CountSystem cs)
        {
            int scale = cs.Scale();
            int irc = (int)Math.Round(cs.Irc(r.Decks) * scale);
            long pos = 0, neg = 0;
            for (int code = 0; code < 20; code++)
            {
                int rank = (code >> 1) + 1;
                int perDeck = rank == 10 ? 8 : 2;
                int t = (int)Math.Round(cs.TagOfCode(code) * scale);
                if (t > 0) pos += (long)t * perDeck * r.Decks; else neg += (long)t * perDeck * r.Decks;
            }
            var b = new BinLookup();
            b.RcMin = (int)Math.Max(-4000, irc + neg);
            b.RcMax = (int)Math.Min(4000, irc + pos);
            b.Width = b.RcMax - b.RcMin + 1;
            int nCards = r.TotalCards;
            b.CrRow = new int[nCards + 1];
            var rows = new System.Collections.Generic.List<short[]>();
            short[] prev = null;
            for (int cr = 0; cr <= nCards; cr++)
            {
                var row = new short[b.Width];
                for (int rc = b.RcMin; rc <= b.RcMax; rc++)
                {
                    int v = MetricValue(cs, scale, rc, cr);
                    if (v < PlayTable.BinMin) v = PlayTable.BinMin; else if (v > PlayTable.BinMax) v = PlayTable.BinMax;
                    row[rc - b.RcMin] = (short)(v - PlayTable.BinMin);
                }
                bool same = prev != null;
                for (int i = 0; same && i < row.Length; i++) same = row[i] == prev[i];
                if (!same) { rows.Add(row); prev = row; }
                b.CrRow[cr] = (rows.Count - 1) * b.Width;
            }
            b.Rows = new short[rows.Count * b.Width];
            for (int i = 0; i < rows.Count; i++) Array.Copy(rows[i], 0, b.Rows, i * b.Width, b.Width);
            return b;
        }

        /// <summary>Per cell: its flags when they are the same at every count (no index play), otherwise 0xFF.</summary>
        internal static byte[] FixedCells(PlayTable t)
        {
            const int CPB = PlayTable.CellsPerBin;
            var f = new byte[CPB];
            for (int c = 0; c < CPB; c++)
            {
                byte v = t.Flags[c];
                bool same = true;
                for (int b = 1; b < PlayTable.NumBins && same; b++) same = t.Flags[b * CPB + c] == v;
                f[c] = same ? v : (byte)0xFF;
            }
            return f;
        }
    }

    /// <summary>
    /// One simulation thread. The hot arrays are pinned and accessed through pointers so the inner loop runs without
    /// bounds checks; every index used is clamped or derived from validated totals, upcards and bins.
    /// </summary>
    internal sealed unsafe class SimWorker : IDisposable
    {
        const int MAXH = 8;
        const int CPB = PlayTable.CellsPerBin;

        // Cards are stored packed: ((tag + TagBias) << 4) | rank, so one load gives both the rank and the count tag.
        const int TagBias = 512;

        readonly int nCards, cut, spots, maxHands, rcMin, rcMax, rcWidth, irc, bj20, spotA, spotB, dblMode;
        readonly bool h17, das, rsa, hsa, peek, takesAll, burn, esPeek, surrAtTurn, insOn, lateSurr;
        readonly bool[] insure;
        readonly int[] shoe;
        readonly int[] baseShoe;
        readonly int[] scratch;
        int pos, rc, roundStart, holeV = -1;
        bool holeHidden, mustShuffle;
        ulong s0, s1, s2, s3;
        ulong rbuf;
        bool rhave;

        readonly List<GCHandle> pins = new List<GCHandle>();
        readonly int* sh;
        readonly short* binRows;
        readonly int* crRow;
        readonly byte* fixedF;
        readonly byte* flags;
        readonly int* p1, p2, status, insured, result, nHands, hTotal, hBet, hf, hs;

        public readonly long[] N, SA, QA, SB, QB, AB;
        public long Rounds, Shoes, Emergency;

        void* Pin(Array a)
        {
            var h = GCHandle.Alloc(a, GCHandleType.Pinned);
            pins.Add(h);
            return (void*)h.AddrOfPinnedObject();
        }

        public void Dispose()
        {
            foreach (var h in pins) if (h.IsAllocated) h.Free();
            pins.Clear();
        }

        public SimWorker(SimSetup su, BinLookup bl, byte[] fixedFlags, ulong seed)
        {
            Rules r = su.Rules;
            CountSystem cs = su.Count;
            nCards = r.TotalCards;
            spots = Math.Max(1, Math.Min(7, su.Spots));
            cut = r.CutCard(spots);
            maxHands = Math.Max(1, Math.Min(MAXH, r.MaxSplitHands));
            h17 = r.H17; das = r.DAS; rsa = r.ResplitAces; hsa = r.HitSplitAces;
            peek = r.HoleCard == HoleCardRule.USPeek;
            takesAll = r.HoleCard == HoleCardRule.ENHC;
            burn = r.BurnCard;
            esPeek = peek && r.Surrender == SurrenderRule.Early;
            surrAtTurn = r.Surrender != SurrenderRule.None && !esPeek;
            lateSurr = r.Surrender == SurrenderRule.Late;
            insOn = r.Insurance;
            bj20 = (int)Math.Round(r.BlackjackPays * 20);
            dblMode = (int)r.DoubleOn;
            int scale = cs.Scale();
            var packed = new int[20];
            for (int code = 0; code < 20; code++)
                packed[code] = (((int)Math.Round(cs.TagOfCode(code) * scale) + TagBias) << 4) | ((code >> 1) + 1);
            irc = (int)Math.Round(cs.Irc(r.Decks) * scale);
            rcMin = bl.RcMin; rcMax = bl.RcMax; rcWidth = bl.Width;
            binRows = (short*)Pin(bl.Rows);
            crRow = (int*)Pin(bl.CrRow);
            fixedF = (byte*)Pin(fixedFlags);
            flags = (byte*)Pin(su.Table.Flags);
            insure = su.Table.Insure;

            baseShoe = new int[nCards];
            int k = 0;
            for (int d = 0; d < r.Decks; d++)
                for (int rk = 1; rk <= 10; rk++)
                {
                    int per = rk == 10 ? 8 : 2;
                    for (int i = 0; i < per; i++) { baseShoe[k++] = packed[(rk - 1) * 2]; baseShoe[k++] = packed[(rk - 1) * 2 + 1]; }
                }
            shoe = (int[])baseShoe.Clone();
            sh = (int*)Pin(shoe);
            scratch = new int[nCards];

            spotA = spots - 1;
            spotB = spots >= 2 ? spots - 2 : -1;
            int* block = (int*)Pin(new int[spots * 6 + spots * MAXH * 2 + MAXH * 2]);
            p1 = block; p2 = p1 + spots; status = p2 + spots; insured = status + spots;
            result = insured + spots; nHands = result + spots;
            hTotal = nHands + spots; hBet = hTotal + spots * MAXH;
            hf = hBet + spots * MAXH; hs = hf + MAXH;
            int nb = PlayTable.NumBins;
            N = new long[nb]; SA = new long[nb]; QA = new long[nb]; SB = new long[nb]; QB = new long[nb]; AB = new long[nb];

            ulong x = seed;
            s0 = SplitMix(ref x); s1 = SplitMix(ref x); s2 = SplitMix(ref x); s3 = SplitMix(ref x);
        }

        static ulong SplitMix(ref ulong x)
        {
            x += 0x9E3779B97F4A7C15UL;
            ulong z = x;
            z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
            z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
            return z ^ (z >> 31);
        }

        ulong Next()
        {
            ulong r = s1 * 5;
            r = ((r << 7) | (r >> 57)) * 9;
            ulong t = s1 << 17;
            s2 ^= s0; s3 ^= s1; s1 ^= s2; s0 ^= s3; s2 ^= t;
            s3 = (s3 << 45) | (s3 >> 19);
            return r;
        }

        /// <summary>Uniform integer in [0, n), using each 64-bit generator output as two 32-bit draws.</summary>
        int NextInt(int n)
        {
            uint x;
            if (rhave) { rhave = false; x = (uint)rbuf; }
            else { rbuf = Next(); rhave = true; x = (uint)(rbuf >> 32); }
            return (int)(((ulong)x * (ulong)n) >> 32);
        }

        public void Reset()
        {
            Array.Clear(N, 0, N.Length); Array.Clear(SA, 0, SA.Length); Array.Clear(QA, 0, QA.Length);
            Array.Clear(SB, 0, SB.Length); Array.Clear(QB, 0, QB.Length); Array.Clear(AB, 0, AB.Length);
            Rounds = 0; Shoes = 0; Emergency = 0;
        }

        /// <summary>
        /// New shoe. Cards are shuffled lazily: each draw takes a uniformly random card from the undealt part of the
        /// array (one Fisher–Yates step), which is equivalent to a full shuffle but only pays for cards actually dealt.
        /// </summary>
        public void Shuffle()
        {
            pos = 0;
            rc = irc;
            mustShuffle = false;
            Shoes++;
            if (burn) DrawHidden();
        }

        /// <summary>
        /// The shoe ran out mid-round: like a dealer, shuffle the discards (not the cards on the table) and continue.
        /// The count restarts from the cards still in view; the whole shoe is reshuffled after this round.
        /// </summary>
        void MidRoundReshuffle()
        {
            if (roundStart <= 0) return;
            int inPlay = nCards - roundStart;
            Array.Copy(shoe, roundStart, scratch, 0, inPlay);
            Array.Copy(shoe, 0, shoe, inPlay, roundStart);
            Array.Copy(scratch, 0, shoe, 0, inPlay);
            int r = irc;
            for (int i = 0; i < inPlay; i++) r += (shoe[i] >> 4) - TagBias;
            if (holeHidden && holeV >= 0) r -= (holeV >> 4) - TagBias;
            rc = r;
            pos = inPlay;
            roundStart = 0;
            mustShuffle = true;
            Emergency++;
        }

        public void Run(long rounds)
        {
            for (long i = 0; i < rounds; i++) PlayRound();
            Rounds += rounds;
        }

        int Bin()
        {
            int cr = nCards - pos;
            if (cr < 0) cr = 0;
            int r = rc;
            if (r < rcMin) r = rcMin; else if (r > rcMax) r = rcMax;
            return binRows[crRow[cr] + (r - rcMin)];
        }

        /// <summary>Decision flags for a cell at the current count; cells without index plays skip the count lookup.</summary>
        byte Fl(int cell)
        {
            byte f = fixedF[cell];
            return f != 0xFF ? f : flags[Bin() * CPB + cell];
        }

        /// <summary>Deals a card face up: returns its rank and adds its tag to the running count.</summary>
        int Draw()
        {
            int v = DrawHidden();
            rc += (v >> 4) - TagBias;
            return v & 15;
        }

        /// <summary>Deals a card face down (packed value, not counted).</summary>
        int DrawHidden()
        {
            if (pos >= nCards)
            {
                MidRoundReshuffle();
                if (pos >= nCards) { Emergency++; return baseShoe[NextInt(nCards)]; }
            }
            int p = pos;
            int j = p + NextInt(nCards - p);
            int v = sh[j];
            sh[j] = sh[p];
            sh[p] = v;
            pos = p + 1;
            return v;
        }

        bool CanDouble(int h, bool soft)
        {
            if (dblMode == 0) return true;
            if (soft) return false;
            if (dblMode == 1) return h >= 9 && h <= 11;
            return h >= 10 && h <= 11;
        }

        bool WantsSurrender(int c1, int c2, int up)
        {
            if (c1 == c2)
            {
                byte pf = Fl(440 + (c1 - 1) * 10 + (up - 1));
                if ((pf & Flag.Surrender) != 0) return true;
                if ((pf & Flag.Split) != 0 && maxHands >= 2) return false;
            }
            int h = c1 + c2;
            bool soft = (c1 == 1 || c2 == 1) && h <= 11;
            int cell = soft ? 220 + (h + 10) * 10 + (up - 1) : h * 10 + (up - 1);
            return (Fl(cell) & Flag.Surrender) != 0;
        }

        int PlayHand(int a, int b, int up, bool split, ref int bet)
        {
            int h = a + b;
            bool ace = a == 1 || b == 1;
            bool soft = ace && h <= 11;
            int best = soft ? h + 10 : h;
            if (best == 21) return 21;
            int cell = soft ? 220 + best * 10 + (up - 1) : h * 10 + (up - 1);
            byte f = Fl(cell);
            if ((f & Flag.Double) != 0 && (!split || das) && CanDouble(h, soft))
            {
                int c = Draw();
                h += c; if (c == 1) ace = true;
                bet = 2;
                if (h > 21) return 99;
                return (ace && h <= 11) ? h + 10 : h;
            }
            while ((f & Flag.Stand) == 0)
            {
                int c = Draw();
                h += c; if (c == 1) ace = true;
                if (h > 21) return 99;
                soft = ace && h <= 11;
                best = soft ? h + 10 : h;
                if (best == 21) return 21;
                cell = soft ? 220 + best * 10 + (up - 1) : h * 10 + (up - 1);
                f = Fl(cell);
            }
            return best;
        }

        void PlaySpot(int s, int up)
        {
            int b0 = s * MAXH;
            int nh = 1;
            hf[0] = p1[s]; hs[0] = p2[s];
            bool split = false;
            for (int h = 0; h < nh; h++)
            {
                int a = hf[h];
                int b = hs[h];
                if (b == 0) b = Draw();
                while (a == b && nh < maxHands && (a != 1 || !split || rsa))
                {
                    if ((Fl(440 + (a - 1) * 10 + (up - 1)) & Flag.Split) == 0) break;
                    hf[nh] = a; hs[nh] = 0; nh++;
                    split = true;
                    b = Draw();
                }
                int bet = 1, total;
                if (split && a == 1 && !hsa) total = b == 1 ? 12 : 11 + b;
                else total = PlayHand(a, b, up, split, ref bet);
                hTotal[b0 + h] = total;
                hBet[b0 + h] = bet;
            }
            nHands[s] = nh;
        }

        void Record(int bin)
        {
            long a = result[spotA];
            N[bin]++; SA[bin] += a; QA[bin] += a * a;
            if (spotB >= 0)
            {
                long b = result[spotB];
                SB[bin] += b; QB[bin] += b * b; AB[bin] += a * b;
            }
        }

        void PlayRound()
        {
            if (pos >= cut || mustShuffle) Shuffle();
            roundStart = pos;
            holeHidden = false;
            holeV = -1;
            int betBin = Bin();
            int S = spots;
            for (int s = 0; s < S; s++) p1[s] = Draw();
            int up = Draw();
            for (int s = 0; s < S; s++) p2[s] = Draw();
            int hole = 0;
            if (peek) { holeV = DrawHidden(); hole = holeV & 15; holeHidden = true; }

            for (int s = 0; s < S; s++)
            {
                insured[s] = 0; nHands[s] = 0;
                status[s] = ((p1[s] == 1 && p2[s] == 10) || (p1[s] == 10 && p2[s] == 1)) ? 2 : 0;
            }
            if (esPeek)
            {
                for (int s = 0; s < S; s++)
                    if (status[s] == 0 && WantsSurrender(p1[s], p2[s], up)) status[s] = 1;
            }
            if (up == 1 && insOn && insure[Bin()])
                for (int s = 0; s < S; s++) if (status[s] != 1) insured[s] = 1;

            bool dealerBJ = false;
            if (peek)
            {
                dealerBJ = (up == 1 && hole == 10) || (up == 10 && hole == 1);
                if (dealerBJ)
                {
                    rc += (holeV >> 4) - TagBias;
                    holeHidden = false;
                    for (int s = 0; s < S; s++)
                    {
                        int res = status[s] == 1 ? -10 : (status[s] == 2 ? 0 : -20);
                        if (insured[s] != 0) res += 20;
                        result[s] = res;
                    }
                    Record(betBin);
                    return;
                }
            }

            bool anyLive = false;
            for (int s = 0; s < S; s++)
            {
                if (status[s] != 0) continue;
                if (surrAtTurn && WantsSurrender(p1[s], p2[s], up)) { status[s] = 1; continue; }
                PlaySpot(s, up);
                if (!anyLive)
                {
                    int b0 = s * MAXH;
                    for (int h = 0; h < nHands[s]; h++) if (hTotal[b0 + h] <= 21) { anyLive = true; break; }
                }
            }

            if (peek) { rc += (holeV >> 4) - TagBias; holeHidden = false; }
            else
            {
                hole = Draw();
                dealerBJ = (up == 1 && hole == 10) || (up == 10 && hole == 1);
            }
            int dh = up + hole;
            bool dace = up == 1 || hole == 1;
            int dFinal = 0;
            if (!dealerBJ)
            {
                if (anyLive)
                {
                    while (true)
                    {
                        int best = (dace && dh <= 11) ? dh + 10 : dh;
                        if (best > 17 || (best == 17 && !(h17 && dace && dh == 7))) break;
                        int c = Draw();
                        dh += c; if (c == 1) dace = true;
                    }
                }
                int bst = (dace && dh <= 11) ? dh + 10 : dh;
                dFinal = bst > 21 ? 0 : bst;
            }

            for (int s = 0; s < S; s++)
            {
                int res;
                int st = status[s];
                if (st == 1) res = dealerBJ && lateSurr ? -20 : -10;
                else if (st == 2) res = dealerBJ ? 0 : bj20;
                else if (dealerBJ)
                {
                    if (takesAll)
                    {
                        int tot = 0, b0 = s * MAXH;
                        for (int h = 0; h < nHands[s]; h++) tot += hBet[b0 + h];
                        res = -20 * tot;
                    }
                    else res = -20;
                }
                else
                {
                    res = 0;
                    int b0 = s * MAXH;
                    for (int h = 0; h < nHands[s]; h++)
                    {
                        int t = hTotal[b0 + h];
                        int bet = hBet[b0 + h] * 20;
                        if (t > 21) res -= bet;
                        else if (dFinal == 0 || t > dFinal) res += bet;
                        else if (t < dFinal) res -= bet;
                    }
                }
                if (insured[s] != 0) res += dealerBJ ? 20 : -10;
                result[s] = res;
            }
            Record(betBin);
        }
    }
}
