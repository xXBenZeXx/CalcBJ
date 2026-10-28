using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Xml.Serialization;
using CalcBJ.Engine;

namespace CalcBJ.App
{
    /// <summary>Everything a saved .bjev file contains.</summary>
    public class ScenarioFile
    {
        public string Version = "1";
        public Rules Rules;
        public CountSystem Count;
        public StrategyConfig Strategy;
        public int Spots;
        public double RoundsPerHour;
        public bool AutoRoundsPerHour;
        public double Unit;
        public double Bankroll;
        public BetRamp Ramp;
        public int RampPreset;
        public long SimRounds;
        public int Threads;
        public bool AutoRun;
        public double TripHours;
        public double TripBankroll;
        public SimData Sim;
        public List<CompareEntry> Compare;
    }

    public class CompareEntry
    {
        public string Name;
        public string Rules, Count, Strategy, Ramp;
        public int Spots;
        public double Unit, RoundsPerHour, Bankroll;
        public double WinPerHour, SdPerHour, WinPer100, SdPer100, EdgePct, Score, DI, N0, HoursN0, ROR, AvgBet, Spread, PlayedPct, CEPerHour, HouseEdge, Bankroll5, KellyUnit;
        public long Rounds;
        public bool Stale;
    }

    internal static class RampPresets
    {
        public static readonly string[] Names =
        {
            "Flat bet (1 unit)", "1–4 spread", "1–8 spread", "1–12 spread", "1–16 spread", "1–24 spread",
            "1–12, wong out at TC ≤ −1", "Back-count: play TC ≥ +1 (2–12)", "(custom ramp)"
        };

        public const int Custom = 8;

        static readonly int[][] Tables =
        {
            //           TC: <=0, 1, 2, 3, 4, 5, >=6       (values for TC <= 0 apply to every lower count)
            new[] { 1, 1, 1, 1, 1, 1, 1 },
            new[] { 1, 1, 2, 3, 4, 4, 4 },
            new[] { 1, 1, 2, 4, 6, 8, 8 },
            new[] { 1, 2, 4, 6, 8, 10, 12 },
            new[] { 1, 2, 4, 8, 12, 16, 16 },
            new[] { 1, 4, 8, 12, 16, 20, 24 },
            new[] { 1, 2, 4, 6, 8, 10, 12 },
            new[] { 0, 2, 4, 6, 8, 10, 12 },
        };

        public static double UnitsAt(int preset, double hiLoTc)
        {
            int tc = (int)Math.Floor(hiLoTc + 1e-9);
            int[] t = Tables[Math.Max(0, Math.Min(Tables.Length - 1, preset))];
            if (preset == 6 && tc <= -1) return 0;
            if (tc <= 0) return t[0];
            if (tc >= 6) return t[6];
            return t[tc];
        }
    }

    /// <summary>Helpers that relate a count's own scale to Hi-Lo true counts.</summary>
    internal static class CountScale
    {
        /// <summary>How many of this count's true-count units equal one Hi-Lo true count (spread of the TC distribution).</summary>
        public static double Factor(CountSystem cs)
        {
            double m = cs.ImbalancePerDeck() / 52.0, K = 0;
            for (int code = 0; code < 20; code++)
            {
                int rank = (code >> 1) + 1;
                double d = cs.TagOfCode(code) - m;
                K += (rank == 10 ? 8 : 2) * d * d;
            }
            return K > 0 ? Math.Sqrt(K / 40.0) : 1;
        }

        public static double HiLoEquivalent(CountSystem cs, Rules r, int metricValue)
        {
            return StrategyBuilder.TcEquivalent(cs, r, metricValue) / Factor(cs);
        }

        /// <summary>Count value (in the count's own metric) that corresponds to a Hi-Lo true count.</summary>
        public static double MetricFor(CountSystem cs, Rules r, double hiLo)
        {
            double dMid = StrategyBuilder.MidDepthDecks(r);
            double rc = hiLo * Factor(cs) * dMid + cs.Irc(r.Decks) + cs.ImbalancePerDeck() * (r.Decks - dMid);
            return cs.Metric == CountMetric.TrueCount ? rc / dMid : rc;
        }
    }

    internal sealed class AppState
    {
        public Rules Rules = new Rules();
        public CountSystem Count = CountSystem.Presets()[0];
        public StrategyConfig Strategy = new StrategyConfig();
        public int Spots = 3;
        public double RoundsPerHour = 105;
        public bool AutoRoundsPerHour = true;
        public double Unit = 25;
        public double Bankroll = 25000;
        public BetRamp Ramp;
        public int RampPreset = 3;
        public long SimRounds = 200000000;
        public int Threads = Environment.ProcessorCount;
        /// <summary>Off: simulations run only when the user presses Run. On: a settings change re-simulates automatically.</summary>
        public bool AutoRun;
        public double TripHours = 40;
        public double TripBankroll = 10000;
        public SimData Sim;
        public bool SimRunning;
        public double SimProgress;
        public string SimStatus = "";
        public readonly List<CompareEntry> Compare = new List<CompareEntry>();
        public string FilePath;

        public event EventHandler ConfigChanged;
        public event EventHandler BettingChanged;
        public event EventHandler SimChanged;
        public event EventHandler CompareChanged;
        public event EventHandler Reloaded;

        PlayTable table;
        AnalysisResult result;
        string resultKey;

        // Finished (or interrupted) simulations by configuration key, most recent last.
        readonly Dictionary<string, SimData> simCache = new Dictionary<string, SimData>();
        readonly List<string> cacheOrder = new List<string>();
        readonly object cacheLock = new object();
        const int CacheSize = 40;

        /// <summary>Earlier results for a configuration, or null.</summary>
        public SimData Cached(string key)
        {
            if (key == null) return null;
            lock (cacheLock)
            {
                SimData d;
                return simCache.TryGetValue(key, out d) ? d : null;
            }
        }

        public void Remember(SimData d)
        {
            if (d == null || d.Key == null || d.Rounds <= 0) return;
            lock (cacheLock)
            {
                SimData old;
                if (simCache.TryGetValue(d.Key, out old) && old.Rounds > d.Rounds) return;
                simCache[d.Key] = d;
                cacheOrder.Remove(d.Key);
                cacheOrder.Add(d.Key);
                while (cacheOrder.Count > CacheSize)
                {
                    simCache.Remove(cacheOrder[0]);
                    cacheOrder.RemoveAt(0);
                }
            }
        }

        public int CacheCount { get { lock (cacheLock) return simCache.Count; } }

        public void ClearCache()
        {
            lock (cacheLock) { simCache.Clear(); cacheOrder.Clear(); }
        }

        public static readonly int[] RphTable = { 0, 209, 139, 105, 84, 70, 60, 52 };

        public AppState()
        {
            ResetRampRange();
            ApplyPreset(RampPreset);
        }

        public static double DefaultRoundsPerHour(int spots)
        {
            return RphTable[Math.Max(1, Math.Min(7, spots))];
        }

        public string SimKey { get { return SimKeyOf(Rules, Count, Strategy, Spots); } }
        public bool SimStale { get { return Sim != null && Sim.Key != SimKey; } }

        public PlayTable Table
        {
            get
            {
                string k = StrategyBuilder.KeyOf(Rules, Count, Strategy);
                if (table == null || table.Key != k) table = StrategyBuilder.Build(Rules, Count, Strategy);
                return table;
            }
        }

        public AnalysisResult Result
        {
            get
            {
                if (Sim == null) return null;
                string k = Sim.Rounds + "|" + Sim.Key + "|" + RampKey() + "|" + Unit + "|" + RoundsPerHour + "|" + Bankroll;
                if (result == null || resultKey != k)
                {
                    result = Analysis.Evaluate(Sim, Ramp, Unit, RoundsPerHour, Bankroll);
                    resultKey = k;
                }
                return result;
            }
        }

        public string RampKey()
        {
            var sb = new StringBuilder();
            sb.Append(Ramp.Min).Append(':').Append(Ramp.Max);
            for (int i = 0; i < Ramp.Rows; i++) sb.Append(',').Append(Ramp.Units[i].ToString(CultureInfo.InvariantCulture)).Append('x').Append(Ramp.Hands[i]);
            return sb.ToString();
        }

        public string RampText()
        {
            var sb = new StringBuilder();
            double last = double.NaN; int lastH = -1;
            for (int v = Ramp.Min; v <= Ramp.Max; v++)
            {
                double u = Ramp.UnitsAt(v); int h = Ramp.HandsAt(v);
                if (u == last && h == lastH) continue;
                if (sb.Length > 0) sb.Append(", ");
                sb.Append(Theme.CountLabel(v, Ramp.Min, Ramp.Max).Replace(" ", "")).Append(':');
                sb.Append(u.ToString("0.##", CultureInfo.InvariantCulture));
                if (h > 1) sb.Append('x').Append(h);
                last = u; lastH = h;
            }
            return sb.ToString();
        }

        public string StrategyText() { return StrategyTextOf(Strategy); }

        public static string StrategyTextOf(StrategyConfig sc)
        {
            string m;
            switch (sc.Mode)
            {
                case IndexMode.BasicOnly: m = "Basic strategy"; break;
                case IndexMode.Illustrious18: m = "Illustrious 18"; break;
                case IndexMode.I18Fab4: m = "I18 + Fab 4"; break;
                default: m = "Full indices " + sc.IndexMin + ".." + sc.IndexMax; break;
            }
            int n = 0;
            foreach (var o in sc.Overrides) if (o.Enabled) n++;
            if (n > 0) m += " + " + n + " custom";
            return m;
        }

        public string CountText() { return CountTextOf(Count); }

        public static string CountTextOf(CountSystem cs)
        {
            string s = cs.Name + " (" + (cs.Metric == CountMetric.TrueCount ? "TC" : "RC");
            if (cs.Metric == CountMetric.TrueCount)
            {
                switch (cs.DeckEst)
                {
                    case DeckEstimation.Exact: s += ", exact decks"; break;
                    case DeckEstimation.QuarterDeck: s += ", ¼-deck"; break;
                    case DeckEstimation.HalfDeck: s += ", ½-deck"; break;
                    default: s += ", full-deck"; break;
                }
                s += ", " + cs.Rounding.ToString().ToLowerInvariant();
            }
            return s + ")";
        }

        public static string SimKeyOf(Rules r, CountSystem cs, StrategyConfig sc, int spots)
        {
            return StrategyBuilder.KeyOf(r, cs, sc) + "#spots=" + spots;
        }

        public const int MaxRampRows = 121;

        /// <summary>Ramp rows for a count: the equivalent of Hi-Lo -3 to +8.</summary>
        public static void RampRangeFor(CountSystem cs, Rules r, out int lo, out int hi)
        {
            lo = (int)Math.Floor(CountScale.MetricFor(cs, r, -3));
            hi = (int)Math.Ceiling(CountScale.MetricFor(cs, r, 8));
            // Running counts of level-2 systems span many rows (Uston SS with 8 decks needs about 110 to reach the
            // equivalent of Hi-Lo +8, where the presets reach their top bet).
            if (hi - lo > MaxRampRows - 1) { lo = (int)Math.Floor(CountScale.MetricFor(cs, r, -2)); hi = lo + MaxRampRows - 1; }
            if (hi <= lo) hi = lo + 1;
            lo = Math.Max(PlayTable.BinMin, lo); hi = Math.Min(PlayTable.BinMax, hi);
        }

        /// <summary>Index range for a count: the equivalent of Hi-Lo -10 to +10.</summary>
        public static void IndexRangeFor(CountSystem cs, Rules r, out int lo, out int hi)
        {
            lo = Math.Max(PlayTable.BinMin, (int)Math.Floor(CountScale.MetricFor(cs, r, -10)));
            hi = Math.Min(PlayTable.BinMax, (int)Math.Ceiling(CountScale.MetricFor(cs, r, 10)));
        }

        /// <summary>Whether a count value means a different true count when the number of decks changes.</summary>
        public static bool DependsOnDecks(CountSystem cs)
        {
            return cs.Metric == CountMetric.RunningCount || !cs.IsBalanced;
        }

        /// <summary>
        /// The ramp for the same count under other rules (another number of decks): a preset is applied again; a custom
        /// ramp is carried over row by row, each row taking the bet of the old row with the nearest Hi-Lo equivalent.
        /// </summary>
        public static BetRamp RampForRules(CountSystem cs, Rules from, Rules to, BetRamp ramp, int preset)
        {
            int lo, hi;
            RampRangeFor(cs, to, out lo, out hi);
            var r = BetRamp.Create(lo, hi, 1);
            for (int v = lo; v <= hi; v++)
            {
                double hl = CountScale.HiLoEquivalent(cs, to, v);
                if (preset >= 0 && preset < RampPresets.Custom)
                {
                    r.Units[v - lo] = RampPresets.UnitsAt(preset, hl);
                    continue;
                }
                int best = ramp.Min;
                double bestD = double.MaxValue;
                for (int w = ramp.Min; w <= ramp.Max; w++)
                {
                    double d = Math.Abs(CountScale.HiLoEquivalent(cs, from, w) - hl);
                    if (d < bestD) { bestD = d; best = w; }
                }
                r.Units[v - lo] = ramp.UnitsAt(best);
                r.Hands[v - lo] = ramp.HandsAt(best);
            }
            return r;
        }

        public void ResetRampRange()
        {
            int lo, hi;
            RampRangeFor(Count, Rules, out lo, out hi);
            Ramp = Ramp == null ? BetRamp.Create(lo, hi, 1) : Ramp.Resize(lo, hi);
        }

        public void ResetIndexRange()
        {
            int lo, hi;
            IndexRangeFor(Count, Rules, out lo, out hi);
            Strategy.IndexMin = lo;
            Strategy.IndexMax = hi;
        }

        public void ApplyPreset(int preset)
        {
            if (preset < 0 || preset >= RampPresets.Custom) preset = 3;
            RampPreset = preset;
            for (int v = Ramp.Min; v <= Ramp.Max; v++)
            {
                double hl = CountScale.HiLoEquivalent(Count, Rules, v);
                Ramp.Units[v - Ramp.Min] = RampPresets.UnitsAt(preset, hl);
                Ramp.Hands[v - Ramp.Min] = 1;
            }
        }

        public void NotifyConfig()
        {
            if (AutoRoundsPerHour) RoundsPerHour = DefaultRoundsPerHour(Spots);
            var h = ConfigChanged;
            if (h != null) h(this, EventArgs.Empty);
        }

        public void NotifyBetting()
        {
            var h = BettingChanged;
            if (h != null) h(this, EventArgs.Empty);
        }

        public void NotifySim()
        {
            var h = SimChanged;
            if (h != null) h(this, EventArgs.Empty);
        }

        public void NotifyCompare()
        {
            var h = CompareChanged;
            if (h != null) h(this, EventArgs.Empty);
        }

        public CompareEntry Snapshot(string name)
        {
            var a = Result;
            var e = new CompareEntry();
            e.Name = name;
            e.Rules = Rules.Describe();
            e.Count = CountText();
            e.Strategy = StrategyText();
            e.Ramp = RampText();
            e.Spots = Spots; e.Unit = Unit; e.RoundsPerHour = RoundsPerHour; e.Bankroll = Bankroll;
            e.HouseEdge = -Table.BasicResult.PlayerEV;
            e.Stale = SimStale;
            if (a != null && a.Valid)
            {
                e.WinPerHour = a.WinPerHour; e.SdPerHour = a.SdPerHour; e.WinPer100 = a.WinPer100; e.SdPer100 = a.SdPer100;
                e.EdgePct = a.EdgePct; e.Score = a.Score; e.DI = a.DI; e.N0 = a.N0; e.HoursN0 = a.HoursN0; e.ROR = a.ROR;
                e.AvgBet = a.AvgBetPlayed * Unit; e.Spread = a.Spread; e.PlayedPct = a.PlayedFrac * 100; e.CEPerHour = a.CEPerHour;
                e.Bankroll5 = a.BankrollForRor(0.05); e.KellyUnit = a.KellyUnit;
                e.Rounds = Sim.Rounds;
            }
            else
            {
                // No simulation, or a ramp that never bets: no results rather than zeros, which would look best.
                e.WinPerHour = e.SdPerHour = e.WinPer100 = e.SdPer100 = e.EdgePct = e.Score = e.DI = double.NaN;
                e.N0 = e.HoursN0 = e.ROR = e.AvgBet = e.Spread = e.PlayedPct = e.CEPerHour = e.Bankroll5 = e.KellyUnit = double.NaN;
                if (Sim != null) e.Rounds = Sim.Rounds;
            }
            return e;
        }

        // ---------------- persistence ----------------

        public ScenarioFile ToFile(bool includeSim)
        {
            var f = new ScenarioFile();
            f.Rules = Rules; f.Count = Count; f.Strategy = Strategy; f.Spots = Spots;
            f.RoundsPerHour = RoundsPerHour; f.AutoRoundsPerHour = AutoRoundsPerHour; f.Unit = Unit; f.Bankroll = Bankroll;
            f.Ramp = Ramp; f.RampPreset = RampPreset; f.SimRounds = SimRounds; f.Threads = Threads; f.AutoRun = AutoRun;
            f.TripHours = TripHours; f.TripBankroll = TripBankroll;
            f.Sim = includeSim ? Sim : null;
            f.Compare = new List<CompareEntry>(Compare);
            return f;
        }

        public void Save(string path, bool includeSim)
        {
            var ser = new XmlSerializer(typeof(ScenarioFile));
            using (var w = new StreamWriter(path, false, new UTF8Encoding(false))) ser.Serialize(w, ToFile(includeSim));
            FilePath = path;
        }

        public void Load(string path)
        {
            var ser = new XmlSerializer(typeof(ScenarioFile));
            ScenarioFile f;
            using (var r = new StreamReader(path)) f = (ScenarioFile)ser.Deserialize(r);
            if (f.Rules != null) Rules = f.Rules;
            if (f.Count != null && f.Count.Tags != null && f.Count.Tags.Length == 10) { Count = f.Count; CountSystem.UpgradeSaved(Count); }
            if (f.Strategy != null) { Strategy = f.Strategy; if (Strategy.Overrides == null) Strategy.Overrides = new List<IndexOverride>(); }
            Spots = Math.Max(1, Math.Min(7, f.Spots));
            RoundsPerHour = f.RoundsPerHour > 0 ? f.RoundsPerHour : DefaultRoundsPerHour(Spots);
            AutoRoundsPerHour = f.AutoRoundsPerHour;
            Unit = f.Unit > 0 ? f.Unit : 25;
            Bankroll = f.Bankroll > 0 ? f.Bankroll : 25000;
            if (f.Ramp != null && f.Ramp.Units != null && f.Ramp.Hands != null && f.Ramp.Units.Length == f.Ramp.Max - f.Ramp.Min + 1) Ramp = f.Ramp;
            else ResetRampRange();
            RampPreset = f.RampPreset;
            if (f.SimRounds > 0) SimRounds = f.SimRounds;
            if (f.Threads > 0) Threads = f.Threads;
            // AutoRun is an app preference, not part of a scenario: opening a file never turns automatic runs on.
            if (f.TripHours > 0) TripHours = f.TripHours;
            if (f.TripBankroll > 0) TripBankroll = f.TripBankroll;
            Sim = f.Sim != null && f.Sim.N != null && f.Sim.N.Length == PlayTable.NumBins ? f.Sim : null;
            Remember(Sim);
            Compare.Clear();
            if (f.Compare != null) Compare.AddRange(f.Compare);
            FilePath = path;
            table = null; result = null;
            var h = Reloaded;
            if (h != null) h(this, EventArgs.Empty);
        }
    }
}
