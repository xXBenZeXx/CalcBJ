using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace CalcBJ.Engine
{
    public enum HoleCardRule { USPeek, ENHC, ENHC_OBO }
    public enum DoubleRule { AnyTwo, NineToEleven, TenToEleven }
    public enum SurrenderRule { None, Late, Early }
    public enum DeckEstimation { Exact, QuarterDeck, HalfDeck, FullDeck }
    public enum TcRounding { Floor, Truncate, Round }
    public enum CountMetric { TrueCount, RunningCount }
    public enum IndexMode { BasicOnly, Illustrious18, I18Fab4, FullIndices }
    public enum HandKind { Hard, Soft, Pair, Insurance }
    public enum PlayKind { Stand, Hit, Double, NoDouble, Split, NoSplit, Surrender, NoSurrender, Insure, NoInsure }

    /// <summary>Table rules. Ranks are 1..10 everywhere in the engine (1 = Ace, 10 = any ten-value card).</summary>
    public class Rules
    {
        public int Decks;
        public bool H17;
        public HoleCardRule HoleCard;
        public double BlackjackPays;
        public DoubleRule DoubleOn;
        public bool DAS;
        public int MaxSplitHands;
        public bool ResplitAces;
        public bool HitSplitAces;
        public SurrenderRule Surrender;
        public bool Insurance;
        /// <summary>Fraction of the shoe dealt before the shuffle (cut card position).</summary>
        public double Penetration;
        public bool BurnCard;

        public Rules()
        {
            Decks = 6; H17 = true; HoleCard = HoleCardRule.USPeek; BlackjackPays = 1.5;
            DoubleOn = DoubleRule.AnyTwo; DAS = true; MaxSplitHands = 4; ResplitAces = false;
            HitSplitAces = false; Surrender = SurrenderRule.Late; Insurance = true;
            Penetration = 0.75; BurnCard = true;
        }

        public Rules Clone() { return (Rules)MemberwiseClone(); }

        public bool CanDoubleTotal(int hard, bool soft)
        {
            switch (DoubleOn)
            {
                case DoubleRule.NineToEleven: return !soft && hard >= 9 && hard <= 11;
                case DoubleRule.TenToEleven: return !soft && hard >= 10 && hard <= 11;
                default: return true;
            }
        }

        /// <summary>
        /// True for early surrender: half the bet is kept even when the dealer has blackjack. Late surrender in a
        /// no-hole-card game loses the whole bet if the dealer later turns out to have blackjack.
        /// </summary>
        public bool SurrenderBeforePeek
        {
            get { return Surrender == SurrenderRule.Early; }
        }

        /// <summary>European no-hole-card where a dealer blackjack takes doubles and splits too.</summary>
        public bool DealerBJTakesAll { get { return HoleCard == HoleCardRule.ENHC; } }

        public int TotalCards { get { return Decks * 52; } }

        public int CutCard(int spots)
        {
            int n = TotalCards;
            if (Penetration <= 0) return 1; // reshuffle after every round
            int cut = (int)Math.Round(n * Penetration);
            int reserve = Math.Min(n / 3, 8 + spots * 6);
            if (cut > n - reserve) cut = n - reserve;
            if (cut < 10) cut = Math.Min(10, n - 1);
            return cut;
        }

        public static string PayoutText(double p)
        {
            if (Math.Abs(p - 1.5) < 1e-9) return "3:2";
            if (Math.Abs(p - 1.2) < 1e-9) return "6:5";
            if (Math.Abs(p - 1.4) < 1e-9) return "7:5";
            if (Math.Abs(p - 1.0) < 1e-9) return "1:1";
            if (Math.Abs(p - 2.0) < 1e-9) return "2:1";
            return p.ToString("0.##", CultureInfo.InvariantCulture) + ":1";
        }

        public string Describe()
        {
            var sb = new StringBuilder();
            sb.Append(Decks).Append(Decks == 1 ? " deck" : " decks");
            sb.Append(H17 ? ", H17" : ", S17");
            if (DoubleOn == DoubleRule.NineToEleven) sb.Append(", D9-11");
            else if (DoubleOn == DoubleRule.TenToEleven) sb.Append(", D10-11");
            else sb.Append(", DOA");
            if (DAS) sb.Append(", DAS");
            if (MaxSplitHands <= 1) sb.Append(", no splits"); else sb.Append(", SP").Append(MaxSplitHands);
            if (ResplitAces) sb.Append(", RSA");
            if (HitSplitAces) sb.Append(", HSA");
            if (Surrender == SurrenderRule.Late) sb.Append(", LS");
            else if (Surrender == SurrenderRule.Early) sb.Append(", ES");
            if (HoleCard == HoleCardRule.ENHC) sb.Append(", ENHC");
            else if (HoleCard == HoleCardRule.ENHC_OBO) sb.Append(", ENHC-OBO");
            sb.Append(", BJ ").Append(PayoutText(BlackjackPays));
            sb.Append(", pen ").Append((Penetration * 100).ToString("0.#", CultureInfo.InvariantCulture)).Append('%');
            return sb.ToString();
        }

        public string Key()
        {
            return string.Format(CultureInfo.InvariantCulture, "{0}|{1}|{2}|{3}|{4}|{5}|{6}|{7}|{8}|{9}|{10}|{11:0.####}|{12}",
                Decks, H17, HoleCard, BlackjackPays, DoubleOn, DAS, MaxSplitHands, ResplitAces, HitSplitAces, Surrender, Insurance, Penetration, BurnCard);
        }
    }

    /// <summary>A card counting system. Tags are given per rank (A,2..9,T); RedTags optionally override red cards.</summary>
    public class CountSystem
    {
        public string Name;
        public double[] Tags;
        public double[] RedTags;
        public double IrcBase;
        public double IrcPerDeck;
        public CountMetric Metric;
        public DeckEstimation DeckEst;
        public TcRounding Rounding;

        public CountSystem()
        {
            Name = "Hi-Lo";
            Tags = new double[] { -1, 1, 1, 1, 1, 1, 0, 0, 0, -1 };
            RedTags = null;
            Metric = CountMetric.TrueCount;
            DeckEst = DeckEstimation.HalfDeck;
            Rounding = TcRounding.Floor;
        }

        public CountSystem Clone()
        {
            var c = (CountSystem)MemberwiseClone();
            c.Tags = (double[])Tags.Clone();
            c.RedTags = RedTags == null ? null : (double[])RedTags.Clone();
            return c;
        }

        /// <summary>Tag for a card code (code = rankIndex*2 + red).</summary>
        public double TagOfCode(int code)
        {
            int ri = code >> 1;
            bool red = (code & 1) == 1;
            if (red && RedTags != null) return RedTags[ri];
            return Tags[ri];
        }

        /// <summary>Average tag of a rank (1..10) across colours.</summary>
        public double RankTag(int rank)
        {
            int ri = rank - 1;
            if (RedTags == null) return Tags[ri];
            return 0.5 * (Tags[ri] + RedTags[ri]);
        }

        /// <summary>Smallest multiplier that makes every tag an integer.</summary>
        public int Scale()
        {
            int[] candidates = { 1, 2, 3, 4, 5, 10 };
            foreach (int s in candidates)
            {
                bool ok = true;
                for (int code = 0; code < 20 && ok; code++)
                {
                    double v = TagOfCode(code) * s;
                    if (Math.Abs(v - Math.Round(v)) > 1e-9) ok = false;
                }
                if (ok) return s;
            }
            return 10;
        }

        /// <summary>Sum of tags over one 52-card deck.</summary>
        public double ImbalancePerDeck()
        {
            double sum = 0;
            for (int r = 1; r <= 10; r++) sum += RankTag(r) * (r == 10 ? 16 : 4);
            return sum;
        }

        public bool IsBalanced { get { return Math.Abs(ImbalancePerDeck()) < 1e-9; } }

        public double Irc(int decks) { return IrcBase + IrcPerDeck * decks; }

        public double Level()
        {
            double m = 0;
            for (int code = 0; code < 20; code++) m = Math.Max(m, Math.Abs(TagOfCode(code)));
            return m;
        }

        public string TagText()
        {
            string[] names = { "A", "2", "3", "4", "5", "6", "7", "8", "9", "T" };
            var sb = new StringBuilder();
            for (int i = 0; i < 10; i++)
            {
                if (i > 0) sb.Append(' ');
                sb.Append(names[i]).Append(':').Append(Fmt(Tags[i]));
                if (RedTags != null && Math.Abs(RedTags[i] - Tags[i]) > 1e-9) sb.Append("/r").Append(Fmt(RedTags[i]));
            }
            return sb.ToString();
        }

        static string Fmt(double v) { return (v > 0 ? "+" : "") + v.ToString("0.##", CultureInfo.InvariantCulture); }

        public string Key()
        {
            var sb = new StringBuilder();
            for (int code = 0; code < 20; code++) sb.Append(TagOfCode(code).ToString("0.###", CultureInfo.InvariantCulture)).Append(',');
            sb.Append(IrcBase.ToString(CultureInfo.InvariantCulture)).Append('|').Append(IrcPerDeck.ToString(CultureInfo.InvariantCulture));
            sb.Append('|').Append(Metric).Append('|').Append(DeckEst).Append('|').Append(Rounding);
            return sb.ToString();
        }

        static CountSystem Make(string name, double[] tags, double ircBase, double ircPerDeck, CountMetric metric)
        {
            var c = new CountSystem();
            c.Name = name; c.Tags = tags; c.IrcBase = ircBase; c.IrcPerDeck = ircPerDeck; c.Metric = metric;
            return c;
        }

        public static List<CountSystem> Presets()
        {
            var list = new List<CountSystem>();
            //                                    A    2    3    4    5    6    7    8    9    T
            list.Add(Make("Hi-Lo", new double[] { -1, 1, 1, 1, 1, 1, 0, 0, 0, -1 }, 0, 0, CountMetric.TrueCount));
            list.Add(Make("Hi-Opt I", new double[] { 0, 0, 1, 1, 1, 1, 0, 0, 0, -1 }, 0, 0, CountMetric.TrueCount));
            list.Add(Make("Hi-Opt II", new double[] { 0, 1, 1, 2, 2, 1, 1, 0, 0, -2 }, 0, 0, CountMetric.TrueCount));
            list.Add(Make("Omega II", new double[] { 0, 1, 1, 2, 2, 2, 1, 0, -1, -2 }, 0, 0, CountMetric.TrueCount));
            list.Add(Make("Zen Count", new double[] { -1, 1, 1, 2, 2, 2, 1, 0, 0, -2 }, 0, 0, CountMetric.TrueCount));
            list.Add(Make("Wong Halves", new double[] { -1, 0.5, 1, 1, 1.5, 1, 0.5, 0, -0.5, -1 }, 0, 0, CountMetric.TrueCount));
            list.Add(Make("Uston APC", new double[] { 0, 1, 2, 2, 3, 2, 2, 1, -1, -3 }, 0, 0, CountMetric.TrueCount));
            list.Add(Make("Revere RPC", new double[] { -2, 1, 2, 2, 2, 2, 1, 0, 0, -2 }, 0, 0, CountMetric.TrueCount));
            list.Add(Make("Canfield Expert", new double[] { 0, 0, 1, 1, 1, 1, 1, 0, -1, -1 }, 0, 0, CountMetric.TrueCount));
            list.Add(Make("Silver Fox", new double[] { -1, 1, 1, 1, 1, 1, 1, 0, -1, -1 }, 0, 0, CountMetric.TrueCount));
            list.Add(Make("KO (Knock-Out)", new double[] { -1, 1, 1, 1, 1, 1, 1, 0, 0, -1 }, 4, -4, CountMetric.RunningCount));
            var red7 = Make("Red Seven", new double[] { -1, 1, 1, 1, 1, 1, 0, 0, 0, -1 }, 0, -2, CountMetric.RunningCount);
            red7.RedTags = new double[] { -1, 1, 1, 1, 1, 1, 1, 0, 0, -1 };
            list.Add(red7);
            list.Add(Make("Uston SS", new double[] { -2, 2, 2, 2, 3, 2, 1, 0, -1, -2 }, 0, -2, CountMetric.RunningCount));
            return list;
        }
    }

    /// <summary>A user-entered index play that replaces the computed/basic decision for one flag of one cell.</summary>
    public class IndexOverride
    {
        public bool Enabled;
        public HandKind Kind;
        public int Total;     // hard/soft total, or pair rank 1..10
        public int Upcard;    // 1..10 (1 = Ace)
        public PlayKind Play;
        public bool AtOrAbove;
        public double Index;

        public IndexOverride() { Enabled = true; Kind = HandKind.Hard; Total = 16; Upcard = 10; Play = PlayKind.Stand; AtOrAbove = true; Index = 0; }

        public string Key()
        {
            return string.Format(CultureInfo.InvariantCulture, "{0}{1}{2}{3}{4}{5}{6}", Enabled ? 1 : 0, Kind, Total, Upcard, Play, AtOrAbove ? ">=" : "<=", Index);
        }
    }

    public class StrategyConfig
    {
        public IndexMode Mode;
        public int IndexMin;
        public int IndexMax;
        public List<IndexOverride> Overrides;

        public StrategyConfig()
        {
            Mode = IndexMode.I18Fab4; IndexMin = -10; IndexMax = 10; Overrides = new List<IndexOverride>();
        }

        public string Key()
        {
            var sb = new StringBuilder();
            sb.Append(Mode).Append('|').Append(IndexMin).Append('|').Append(IndexMax);
            foreach (var o in Overrides) sb.Append('|').Append(o.Key());
            return sb.ToString();
        }
    }

    public static class Cards
    {
        public static readonly string[] UpLabels = { "", "A", "2", "3", "4", "5", "6", "7", "8", "9", "T" };
        /// <summary>Dealer upcards in the usual chart order 2..9, T, A.</summary>
        public static readonly int[] ChartUpOrder = { 2, 3, 4, 5, 6, 7, 8, 9, 10, 1 };
        public static string RankName(int r) { return UpLabels[r]; }
    }
}
