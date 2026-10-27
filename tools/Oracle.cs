using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using CalcBJ.Engine;

/// <summary>
/// Writes reference results of the C# engine as JSON fixtures. The Rust port in web\engine is tested against them.
/// Built and run by web\engine\make-fixtures.ps1. Usage: Oracle.exe [output folder]
/// </summary>
static class Oracle
{
    static void Main(string[] args)
    {
        Thread.CurrentThread.CurrentCulture = CultureInfo.InvariantCulture;
        string dir = args.Length > 0 ? args[0] : "fixtures";
        Directory.CreateDirectory(dir);
        Save(dir, "ca.json", CaFixture());
        Save(dir, "counts.json", CountFixture());
        Save(dir, "strategy.json", StrategyFixture());
        Save(dir, "sim.json", SimFixture());
        Save(dir, "analysis.json", AnalysisFixture());
    }

    static void Save(string dir, string name, JsonWriter w)
    {
        string path = Path.Combine(dir, name);
        File.WriteAllText(path, w.ToString(), new UTF8Encoding(false));
        Console.WriteLine("{0,-14} {1,11:N0} bytes", name, new FileInfo(path).Length);
    }

    // ---------------- Inputs ----------------

    static Rules R(int decks, bool h17, bool das, SurrenderRule sur, int maxSplit, bool rsa)
    {
        var r = new Rules();
        r.Decks = decks; r.H17 = h17; r.DAS = das; r.Surrender = sur; r.MaxSplitHands = maxSplit; r.ResplitAces = rsa;
        return r;
    }

    static Rules RulesPen(int decks, double pen)
    {
        var r = new Rules();
        r.Decks = decks; r.Penetration = pen;
        return r;
    }

    sealed class RuleCase
    {
        public string Label;
        public Rules Rules;
        public RuleCase(string label, Rules rules) { Label = label; Rules = rules; }
    }

    static List<RuleCase> RuleCases()
    {
        var list = new List<RuleCase>();
        list.Add(new RuleCase("8D S17 DAS noSurr SP4", R(8, false, true, SurrenderRule.None, 4, false)));
        list.Add(new RuleCase("6D S17 DAS noSurr SP4", R(6, false, true, SurrenderRule.None, 4, false)));
        list.Add(new RuleCase("6D H17 DAS noSurr SP4", R(6, true, true, SurrenderRule.None, 4, false)));
        list.Add(new RuleCase("6D H17 DAS LS SP4 (defaults)", new Rules()));
        list.Add(new RuleCase("6D S17 DAS LS SP4 RSA", R(6, false, true, SurrenderRule.Late, 4, true)));
        list.Add(new RuleCase("2D H17 DAS noSurr SP4", R(2, true, true, SurrenderRule.None, 4, false)));
        var d2 = R(2, false, true, SurrenderRule.None, 4, false); d2.DoubleOn = DoubleRule.NineToEleven;
        list.Add(new RuleCase("2D S17 DAS D9-11", d2));
        list.Add(new RuleCase("1D H17 noDAS SP2", R(1, true, false, SurrenderRule.None, 2, false)));
        var d1 = R(1, false, false, SurrenderRule.None, 2, false); d1.DoubleOn = DoubleRule.TenToEleven; d1.Penetration = 0.625;
        list.Add(new RuleCase("1D S17 noDAS SP2 D10-11 pen 62.5", d1));
        var e1 = R(6, false, true, SurrenderRule.None, 4, false); e1.HoleCard = HoleCardRule.ENHC;
        list.Add(new RuleCase("6D S17 DAS ENHC", e1));
        var e2 = R(6, false, true, SurrenderRule.Late, 4, false); e2.HoleCard = HoleCardRule.ENHC;
        list.Add(new RuleCase("6D S17 DAS ENHC LS", e2));
        var e3 = R(4, false, true, SurrenderRule.None, 4, true); e3.HitSplitAces = true; e3.HoleCard = HoleCardRule.ENHC_OBO;
        list.Add(new RuleCase("4D S17 DAS RSA HSA OBO", e3));
        var e4 = R(6, true, true, SurrenderRule.None, 4, false); e4.BlackjackPays = 1.2; e4.DoubleOn = DoubleRule.TenToEleven;
        list.Add(new RuleCase("6D H17 DAS 6:5 D10-11", e4));
        list.Add(new RuleCase("6D S17 DAS ES", R(6, false, true, SurrenderRule.Early, 4, false)));
        var e6 = R(8, true, false, SurrenderRule.Early, 3, false); e6.BlackjackPays = 1.4; e6.Insurance = false;
        list.Add(new RuleCase("8D H17 ES SP3 7:5 no insurance", e6));
        var e7 = R(6, false, true, SurrenderRule.Early, 1, false); e7.HoleCard = HoleCardRule.ENHC_OBO;
        list.Add(new RuleCase("6D S17 no splits ES OBO", e7));
        list.Add(new RuleCase("5D H17 LS RSA 2:1 pen 83 no burn", FiveDeck()));
        var e9 = R(2, true, true, SurrenderRule.Late, 2, false); e9.Penetration = 0; e9.BlackjackPays = 1.25;
        list.Add(new RuleCase("2D H17 LS SP2 BJ 1.25 CSM", e9));
        return list;
    }

    static Rules FiveDeck()
    {
        var r = R(5, true, true, SurrenderRule.Late, 4, true);
        r.Penetration = 0.83; r.BurnCard = false; r.BlackjackPays = 2.0;
        return r;
    }

    static CountSystem Preset(string name)
    {
        foreach (var c in CountSystem.Presets()) if (c.Name == name) return c;
        throw new ArgumentException(name);
    }

    static CountSystem RcFractional()
    {
        var c = Preset("Hi-Lo");
        c.Name = "Hi-Lo RC, fractional IRC"; c.Metric = CountMetric.RunningCount; c.IrcBase = 3; c.IrcPerDeck = -0.5;
        return c;
    }

    static CountSystem CustomRed()
    {
        var c = Preset("Wong Halves");
        c.Name = "Custom red tags";
        c.RedTags = new double[] { -1, 0.5, 1, 1, 1.5, 1, 1, 0, 0, -1 };
        return c;
    }

    static List<CountSystem> CountCases()
    {
        var list = CountSystem.Presets();
        var a = Preset("Hi-Lo"); a.Name = "Hi-Lo exact/round"; a.DeckEst = DeckEstimation.Exact; a.Rounding = TcRounding.Round; list.Add(a);
        var b = Preset("Wong Halves"); b.Name = "Halves quarter/truncate"; b.DeckEst = DeckEstimation.QuarterDeck; b.Rounding = TcRounding.Truncate; list.Add(b);
        var c = Preset("Zen Count"); c.Name = "Zen full-deck"; c.DeckEst = DeckEstimation.FullDeck; list.Add(c);
        list.Add(RcFractional());
        list.Add(CustomRed());
        return list;
    }

    static StrategyConfig Sc(IndexMode mode, int lo, int hi)
    {
        var s = new StrategyConfig();
        s.Mode = mode; s.IndexMin = lo; s.IndexMax = hi;
        return s;
    }

    static IndexOverride Ov(HandKind kind, int total, int up, PlayKind play, bool atOrAbove, double index)
    {
        var o = new IndexOverride();
        o.Kind = kind; o.Total = total; o.Upcard = up; o.Play = play; o.AtOrAbove = atOrAbove; o.Index = index;
        return o;
    }

    static StrategyConfig WithOverrides()
    {
        var s = new StrategyConfig();
        s.Overrides.Add(Ov(HandKind.Hard, 16, 10, PlayKind.Stand, true, 1));
        s.Overrides.Add(Ov(HandKind.Insurance, 0, 1, PlayKind.Insure, true, 2.5));
        s.Overrides.Add(Ov(HandKind.Pair, 10, 6, PlayKind.Split, true, 4));
        s.Overrides.Add(Ov(HandKind.Soft, 19, 6, PlayKind.Double, true, 1));
        s.Overrides.Add(Ov(HandKind.Hard, 12, 2, PlayKind.Hit, false, 2));
        s.Overrides.Add(Ov(HandKind.Hard, 15, 10, PlayKind.Surrender, true, 0));
        s.Overrides.Add(Ov(HandKind.Pair, 8, 10, PlayKind.NoSplit, true, 8));
        s.Overrides.Add(Ov(HandKind.Hard, 11, 1, PlayKind.NoDouble, false, -2));
        var off = Ov(HandKind.Hard, 13, 2, PlayKind.Hit, true, -1); off.Enabled = false;
        s.Overrides.Add(off);
        s.Overrides.Add(Ov(HandKind.Soft, 10, 5, PlayKind.Double, true, 0)); // not a soft total: ignored
        s.Overrides.Add(Ov(HandKind.Hard, 9, 2, PlayKind.Double, true, 0.7));
        return s;
    }

    // ---------------- ca.json ----------------

    static JsonWriter CaFixture()
    {
        var w = new JsonWriter();
        var hiLo = new CountSystem();
        var omega = Preset("Omega II");
        w.Obj();
        w.Key("cases").Arr();
        foreach (var c in RuleCases())
        {
            var r = c.Rules;
            w.Obj();
            w.Key("label").Val(c.Label);
            w.Key("rules"); RulesJson(w, r);
            w.Key("key").Val(r.Key());
            w.Key("describe").Val(r.Describe());
            w.Key("payoutText").Val(Rules.PayoutText(r.BlackjackPays));
            w.Key("totalCards").Val(r.TotalCards);
            w.Key("cutCard").Arr();
            for (int s = 1; s <= 7; s++) w.Val(r.CutCard(s));
            w.End();
            w.Key("midDepthDecks").Val(StrategyBuilder.MidDepthDecks(r));
            var dbl = new StringBuilder();
            for (int soft = 0; soft <= 1; soft++)
                for (int t = 2; t <= 21; t++) dbl.Append(r.CanDoubleTotal(t, soft == 1) ? '1' : '0');
            w.Key("canDouble").Val(dbl.ToString());
            var eng = new CAEngine(r);
            w.Key("offTop"); CaJson(w, eng.Evaluate(CAEngine.FullShoe(r.Decks)), true);
            w.Key("houseEdge").Val(CAEngine.HouseEdgeOffTop(r));
            var dep = CAEngine.FullShoe(r.Decks);
            dep[1] -= 1; dep[5] -= 3; dep[6] -= 2; dep[10] -= 4;
            var shoes = new List<KeyValuePair<string, double[]>>();
            shoes.Add(new KeyValuePair<string, double[]>("Hi-Lo +4 at mid depth", StrategyBuilder.ShiftedShoe(hiLo, 4, StrategyBuilder.MidDepthDecks(r))));
            shoes.Add(new KeyValuePair<string, double[]>("Omega II -3 at 40%", StrategyBuilder.ShiftedShoe(omega, -3, r.Decks * 0.4)));
            shoes.Add(new KeyValuePair<string, double[]>("depleted", dep));
            w.Key("shoes").Arr();
            foreach (var kv in shoes)
            {
                w.Obj().Key("label").Val(kv.Key).Key("shoe").Val(kv.Value).Key("result");
                CaJson(w, eng.Evaluate(kv.Value), false);
                w.End();
            }
            w.End();
            w.End();
        }
        w.End();

        w.Key("dealer").Arr();
        for (int hi = 0; hi <= 1; hi++)
            for (int up = 1; up <= 10; up++)
            {
                var dag = DealerDag.Get(up, hi == 1);
                var mass = new double[dag.Count];
                var c6 = CAEngine.FullShoe(6); c6[up] -= 1;
                var d6 = new double[7];
                dag.Evaluate(c6, Sum(c6), mass, d6);
                var c1 = CAEngine.FullShoe(1); c1[up] -= 1; c1[10] -= 1; c1[6] -= 1;
                var d1 = new double[7];
                dag.Evaluate(c1, Sum(c1), mass, d1);
                w.Obj().Key("up").Val(up).Key("h17").Val(hi == 1).Key("count").Val(dag.Count)
                    .Key("shoe6").Val(c6).Key("dist6").Val(d6).Key("shoe1").Val(c1).Key("dist1").Val(d1).End();
            }
        w.End();
        w.End();
        return w;
    }

    static double Sum(double[] c)
    {
        double n = 0;
        for (int r = 1; r <= 10; r++) n += c[r];
        return n;
    }

    static void CaJson(JsonWriter w, CAResult res, bool full)
    {
        w.Obj();
        w.Key("playerEV").Val(res.PlayerEV);
        w.Key("insuranceEV").Val(res.InsuranceEV);
        w.Key("insure").Val(res.Insure);
        w.Key("flags").Val(Hex(res.Flags, 0, res.Flags.Length));
        if (full) w.Key("cellEV").Val(res.CellEV);
        else
        {
            double sum = 0;
            int nan = 0;
            foreach (double v in res.CellEV) { if (double.IsNaN(v)) nan++; else sum += v; }
            w.Key("cellEVSum").Val(sum).Key("cellEVNaN").Val(nan);
        }
        w.End();
    }

    // ---------------- counts.json ----------------

    static JsonWriter CountFixture()
    {
        var w = new JsonWriter();
        var rulesList = new Rules[] { new Rules(), RulesPen(1, 0.625), RulesPen(8, 0.83) };
        w.Obj();
        w.Key("rules").Arr();
        foreach (var r in rulesList) RulesJson(w, r);
        w.End();
        w.Key("systems").Arr();
        foreach (var cs in CountCases())
        {
            w.Obj();
            w.Key("count"); CountJson(w, cs);
            w.Key("scale").Val(cs.Scale());
            w.Key("imbalance").Val(cs.ImbalancePerDeck());
            w.Key("balanced").Val(cs.IsBalanced);
            w.Key("level").Val(cs.Level());
            w.Key("irc").Arr();
            foreach (int d in new[] { 1, 2, 5, 6, 8 }) w.Val(cs.Irc(d));
            w.End();
            w.Key("tagText").Val(cs.TagText());
            w.Key("key").Val(cs.Key());
            w.Key("tagOfCode").Arr();
            for (int code = 0; code < 20; code++) w.Val(cs.TagOfCode(code));
            w.End();
            w.Key("rankTag").Arr();
            for (int r = 1; r <= 10; r++) w.Val(cs.RankTag(r));
            w.End();
            w.Key("representative").Arr();
            foreach (int v in new[] { -12, -1, 0, 1, 7 }) w.Val(StrategyBuilder.Representative(cs, v));
            w.End();
            w.Key("tcEquivalent").Arr();
            foreach (var r in rulesList)
            {
                w.Arr();
                foreach (double mv in new[] { -10, -3.5, 0, 2.5, 9 }) w.Val(StrategyBuilder.TcEquivalent(cs, r, mv));
                w.End();
            }
            w.End();
            w.Key("shifted").Arr();
            foreach (double t in new[] { -6, -2.5, 0, 1, 3.7, 8, 25 })
                foreach (double dr in new[] { 0.5, 2.75, 5.0 })
                    w.Obj().Key("tcEq").Val(t).Key("decks").Val(dr).Key("shoe").Val(StrategyBuilder.ShiftedShoe(cs, t, dr)).End();
            w.End();
            int scale = cs.Scale();
            w.Key("metricValue").Arr();
            foreach (int rc in new[] { -60, -13, -7, -1, 0, 1, 2, 5, 12, 40 })
                foreach (int cr in new[] { 0, 1, 5, 6, 13, 25, 26, 27, 51, 52, 77, 78, 150, 260, 312 })
                    w.Arr().Val(rc).Val(cr).Val(Simulator.MetricValue(cs, scale, rc, cr)).End();
            w.End();
            w.Key("binLookups").Arr();
            foreach (var r in rulesList)
            {
                var b = Simulator.BuildBins(r, cs);
                w.Obj().Key("rcMin").Val(b.RcMin).Key("rcMax").Val(b.RcMax).Key("width").Val(b.Width)
                    .Key("rows").Val(b.Rows.Length / b.Width).Key("crRow").Val(b.CrRow).Key("rowsHash").Val(Fnv(b.Rows)).End();
            }
            w.End();
            w.End();
        }
        w.End();
        w.Key("estimateDecks").Obj();
        foreach (DeckEstimation est in Enum.GetValues(typeof(DeckEstimation)))
        {
            w.Key(est.ToString()).Arr();
            for (int cr = 0; cr <= 416; cr++) w.Val(Simulator.EstimateDecks(est, cr));
            w.End();
        }
        w.End();
        w.End();
        return w;
    }

    // ---------------- strategy.json ----------------

    sealed class StratCase
    {
        public string Label;
        public Rules Rules;
        public CountSystem Count;
        public StrategyConfig Config;
        public StratCase(string label, Rules r, CountSystem c, StrategyConfig s) { Label = label; Rules = r; Count = c; Config = s; }
    }

    static List<StratCase> StrategyCases()
    {
        var hiLo = new CountSystem();
        var list = new List<StratCase>();
        list.Add(new StratCase("6D S17 DAS LS, Hi-Lo full -10..10", R(6, false, true, SurrenderRule.Late, 4, false), hiLo, Sc(IndexMode.FullIndices, -10, 10)));
        list.Add(new StratCase("6D H17 LS defaults, Hi-Lo I18F4", new Rules(), hiLo, new StrategyConfig()));
        list.Add(new StratCase("6D H17 LS, Hi-Lo I18", new Rules(), hiLo, Sc(IndexMode.Illustrious18, -10, 10)));
        list.Add(new StratCase("6D H17 LS, basic", new Rules(), hiLo, Sc(IndexMode.BasicOnly, -10, 10)));
        list.Add(new StratCase("2D H17 noSurr, KO I18F4 -40..30", R(2, true, true, SurrenderRule.None, 4, false), Preset("KO (Knock-Out)"), Sc(IndexMode.I18Fab4, -40, 30)));
        list.Add(new StratCase("6D S17 LS, KO I18F4 -30..30", R(6, false, true, SurrenderRule.Late, 4, false), Preset("KO (Knock-Out)"), Sc(IndexMode.I18Fab4, -30, 30)));
        list.Add(new StratCase("1D H17 noDAS SP2, Hi-Lo full", R(1, true, false, SurrenderRule.None, 2, false), hiLo, Sc(IndexMode.FullIndices, -10, 10)));
        list.Add(new StratCase("6D S17 LS, Red Seven full -20..20", R(6, false, true, SurrenderRule.Late, 4, false), Preset("Red Seven"), Sc(IndexMode.FullIndices, -20, 20)));
        var omega = Preset("Omega II"); omega.DeckEst = DeckEstimation.Exact; omega.Rounding = TcRounding.Round;
        list.Add(new StratCase("8D S17 DAS, Omega II exact/round full -8..12", R(8, false, true, SurrenderRule.None, 4, false), omega, Sc(IndexMode.FullIndices, -8, 12)));
        var halves = Preset("Wong Halves"); halves.DeckEst = DeckEstimation.QuarterDeck; halves.Rounding = TcRounding.Truncate;
        list.Add(new StratCase("6D H17 LS, Halves quarter/truncate I18", new Rules(), halves, Sc(IndexMode.Illustrious18, -10, 10)));
        list.Add(new StratCase("6D H17 LS, Hi-Lo I18F4 + overrides", new Rules(), hiLo, WithOverrides()));
        var enhc = R(6, false, true, SurrenderRule.None, 4, false); enhc.HoleCard = HoleCardRule.ENHC;
        list.Add(new StratCase("6D S17 DAS ENHC, Hi-Lo full", enhc, hiLo, Sc(IndexMode.FullIndices, -10, 10)));
        var csm = R(2, true, true, SurrenderRule.Late, 2, false); csm.Penetration = 0;
        list.Add(new StratCase("2D H17 LS SP2 CSM, Hi-Lo full -6..6", csm, hiLo, Sc(IndexMode.FullIndices, -6, 6)));
        list.Add(new StratCase("1D S17 noDAS SP2, custom red tags full -5..9", R(1, false, false, SurrenderRule.None, 2, false), CustomRed(), Sc(IndexMode.FullIndices, -5, 9)));
        list.Add(new StratCase("5D H17 LS RSA 2:1 pen 83, Hi-Lo RC fractional IRC I18F4", FiveDeck(), RcFractional(), Sc(IndexMode.I18Fab4, -10, 10)));
        list.Add(new StratCase("6D H17 LS, Hi-Lo full, reversed range 4..-3", new Rules(), hiLo, Sc(IndexMode.FullIndices, 4, -3)));
        return list;
    }

    static JsonWriter StrategyFixture()
    {
        const int CPB = PlayTable.CellsPerBin;
        var w = new JsonWriter();
        w.Obj();
        w.Key("cases").Arr();
        foreach (var c in StrategyCases())
        {
            var pt = StrategyBuilder.Build(c.Rules, c.Count, c.Config);
            w.Obj();
            w.Key("label").Val(c.Label);
            w.Key("rules"); RulesJson(w, c.Rules);
            w.Key("count"); CountJson(w, c.Count);
            w.Key("config"); ConfigJson(w, c.Config);
            w.Key("key").Val(pt.Key);
            w.Key("flagsHash").Val(Fnv(pt.Flags));
            w.Key("flagsBins").Arr();
            for (int b = 0; b < PlayTable.NumBins; b++)
            {
                bool same = b > 0;
                for (int i = 0; same && i < CPB; i++) same = pt.Flags[b * CPB + i] == pt.Flags[(b - 1) * CPB + i];
                if (!same) w.Arr().Val(b).Val(Hex(pt.Flags, b * CPB, CPB)).End();
            }
            w.End();
            var ins = new StringBuilder();
            foreach (bool x in pt.Insure) ins.Append(x ? '1' : '0');
            w.Key("insure").Val(ins.ToString());
            w.Key("binTcEq").Val(pt.BinTcEq);
            w.Key("binEV").Val(pt.BinEV);
            w.Key("basicEV").Val(pt.BasicResult.PlayerEV);
            w.Key("basicFlags").Val(Hex(pt.Basic, 0, CPB));
            w.Key("perBinMin").Val(pt.PerBinMin);
            if (pt.PerBin == null) w.Key("perBin").Null();
            else
            {
                w.Key("perBin").Arr();
                foreach (var res in pt.PerBin) CaJson(w, res, false);
                w.End();
            }
            w.Key("fixedCells").Val(Hex(Simulator.FixedCells(pt), 0, CPB));
            int lo = Math.Min(c.Config.IndexMin, c.Config.IndexMax), hi = Math.Max(c.Config.IndexMin, c.Config.IndexMax);
            w.Key("describeRange").Arr().Val(lo).Val(hi).End();
            w.Key("indices").Arr();
            foreach (var info in StrategyBuilder.DescribeIndices(pt, lo, hi))
                w.Obj().Key("kind").Val(info.Kind.ToString()).Key("total").Val(info.Total).Key("upcard").Val(info.Upcard)
                    .Key("hand").Val(info.Hand).Key("play").Val(info.Play).Key("condition").Val(info.Condition)
                    .Key("threshold").Val(info.Threshold).Key("atOrAbove").Val(info.AtOrAbove).Key("irregular").Val(info.Irregular)
                    .Key("text").Val(info.Text).End();
            w.End();
            var codes = new StringBuilder();
            for (int cell = 0; cell < CPB; cell++)
            {
                if (cell > 0) codes.Append(',');
                codes.Append(StrategyBuilder.ActionCode(pt.Basic[cell], cell >= 440));
            }
            w.Key("actionCodes").Val(codes.ToString());
            w.End();
        }
        w.End();
        w.End();
        return w;
    }

    // ---------------- sim.json ----------------

    sealed class SimCase
    {
        public string Label;
        public Rules Rules;
        public CountSystem Count;
        public StrategyConfig Config;
        public int Spots;
        public long Rounds;
        public ulong Seed;
    }

    static SimCase Sim(string label, Rules r, CountSystem cs, StrategyConfig sc, int spots, long rounds, ulong seed)
    {
        var c = new SimCase();
        c.Label = label; c.Rules = r; c.Count = cs; c.Config = sc; c.Spots = spots; c.Rounds = rounds; c.Seed = seed;
        return c;
    }

    static List<SimCase> SimCases()
    {
        var hiLo = new CountSystem();
        var list = new List<SimCase>();
        list.Add(Sim("6D H17 LS, Hi-Lo I18F4, 1 spot", new Rules(), hiLo, new StrategyConfig(), 1, 1000000, 99));
        list.Add(Sim("6D H17 LS, Hi-Lo I18F4, 3 spots", new Rules(), hiLo, new StrategyConfig(), 3, 600001, 1234));
        list.Add(Sim("6D H17 LS, Hi-Lo full, 7 spots", new Rules(), hiLo, Sc(IndexMode.FullIndices, -10, 10), 7, 400000, 5));
        list.Add(Sim("2D H17 noSurr, KO I18F4, 2 spots", R(2, true, true, SurrenderRule.None, 4, false), Preset("KO (Knock-Out)"), Sc(IndexMode.I18Fab4, -40, 30), 2, 1000000, 7));
        list.Add(Sim("1D H17 noDAS SP2, Hi-Lo full, 1 spot", R(1, true, false, SurrenderRule.None, 2, false), hiLo, Sc(IndexMode.FullIndices, -10, 10), 1, 1000000, 8));
        list.Add(Sim("1D S17 SP2, Hi-Lo I18F4, 7 spots (reshuffles)", R(1, false, true, SurrenderRule.None, 2, false), hiLo, new StrategyConfig(), 7, 300000, 9));
        var enhc = R(6, false, true, SurrenderRule.Late, 4, false); enhc.HoleCard = HoleCardRule.ENHC; enhc.Penetration = 0;
        list.Add(Sim("6D S17 DAS ENHC LS CSM, basic, 1 spot", enhc, hiLo, Sc(IndexMode.BasicOnly, -10, 10), 1, 1000000, 10));
        var obo = R(4, false, true, SurrenderRule.Early, 4, true); obo.HitSplitAces = true; obo.HoleCard = HoleCardRule.ENHC_OBO; obo.BurnCard = false;
        list.Add(Sim("4D S17 DAS RSA HSA OBO ES no burn, Hi-Lo full, 2 spots", obo, hiLo, Sc(IndexMode.FullIndices, -10, 10), 2, 1000000, 11));
        var es = R(8, true, false, SurrenderRule.Early, 3, false); es.BlackjackPays = 1.4; es.Insurance = false;
        list.Add(Sim("8D H17 ES SP3 7:5 no insurance, Red Seven I18F4, 3 spots", es, Preset("Red Seven"), Sc(IndexMode.I18Fab4, -20, 20), 3, 500000, 12));
        var halves = Preset("Wong Halves"); halves.DeckEst = DeckEstimation.QuarterDeck; halves.Rounding = TcRounding.Truncate;
        list.Add(Sim("6D S17 LS, Halves quarter/truncate full, 3 spots", R(6, false, true, SurrenderRule.Late, 4, false), halves, Sc(IndexMode.FullIndices, -10, 10), 3, 500000, 13));
        list.Add(Sim("6D H17 LS, Hi-Lo I18F4 + overrides, 2 spots", new Rules(), hiLo, WithOverrides(), 2, 500000, 14));
        var omega = Preset("Omega II"); omega.DeckEst = DeckEstimation.Exact; omega.Rounding = TcRounding.Round;
        list.Add(Sim("8D S17 DAS, Omega II exact/round full, 5 spots", R(8, false, true, SurrenderRule.None, 4, false), omega, Sc(IndexMode.FullIndices, -8, 12), 5, 300000, 15));
        var csm = R(2, true, true, SurrenderRule.Late, 2, false); csm.Penetration = 0; csm.BlackjackPays = 1.25;
        list.Add(Sim("2D H17 LS SP2 BJ 1.25 CSM, basic, 4 spots", csm, hiLo, Sc(IndexMode.BasicOnly, -10, 10), 4, 500000, 16));
        list.Add(Sim("5D H17 LS RSA 2:1 pen 83 no burn, Hi-Lo RC fractional IRC I18F4, 2 spots", FiveDeck(), RcFractional(), Sc(IndexMode.I18Fab4, -10, 10), 2, 500000, 17));
        var nosplit = R(6, false, true, SurrenderRule.Early, 1, false); nosplit.HoleCard = HoleCardRule.ENHC_OBO;
        list.Add(Sim("6D S17 no splits ES OBO, custom red tags full, 1 spot", nosplit, CustomRed(), Sc(IndexMode.FullIndices, -5, 9), 1, 500000, 18));
        list.Add(Sim("6D H17 LS, Hi-Lo I18F4, 1 spot, seed near 2^64", new Rules(), hiLo, new StrategyConfig(), 1, 300000, 0xFFFFFFFFFFFFFFF0UL));
        return list;
    }

    static SimData RunSim(Rules r, CountSystem cs, PlayTable pt, int spots, long rounds, ulong seed)
    {
        var su = new SimSetup();
        su.Rules = r; su.Count = cs; su.Table = pt; su.Spots = spots; su.Rounds = rounds; su.Threads = 1; su.Seed = seed;
        var sim = new Simulator(su);
        var done = new ManualResetEvent(false);
        sim.Finished += (s, e) => done.Set();
        sim.Start();
        done.WaitOne();
        if (sim.Error != null) throw sim.Error;
        return sim.Snapshot();
    }

    static JsonWriter SimFixture()
    {
        var w = new JsonWriter();
        w.Obj();
        w.Key("cases").Arr();
        foreach (var c in SimCases())
        {
            var pt = StrategyBuilder.Build(c.Rules, c.Count, c.Config);
            var d = RunSim(c.Rules, c.Count, pt, c.Spots, c.Rounds, c.Seed);
            w.Obj();
            w.Key("label").Val(c.Label);
            w.Key("rules"); RulesJson(w, c.Rules);
            w.Key("count"); CountJson(w, c.Count);
            w.Key("config"); ConfigJson(w, c.Config);
            w.Key("spots").Val(c.Spots).Key("rounds").Val(c.Rounds).Key("seed").Val(c.Seed.ToString(CultureInfo.InvariantCulture));
            w.Key("flagsHash").Val(Fnv(pt.Flags));
            w.Key("data"); SimDataJson(w, d);
            w.End();
        }
        w.End();
        w.End();
        return w;
    }

    static void SimDataJson(JsonWriter w, SimData d)
    {
        w.Obj();
        w.Key("binMin").Val(d.BinMin).Key("numBins").Val(d.NumBins).Key("spots").Val(d.Spots);
        w.Key("rounds").Val(d.Rounds).Key("shoes").Val(d.Shoes).Key("emergency").Val(d.Emergency);
        w.Key("n").Val(d.N).Key("sa").Val(d.SA).Key("qa").Val(d.QA).Key("sb").Val(d.SB).Key("qb").Val(d.QB).Key("ab").Val(d.AB);
        w.End();
    }

    // ---------------- analysis.json ----------------

    sealed class RampCase
    {
        public BetRamp Ramp;
        public double Unit, Rph, Bankroll;
    }

    static RampCase Rc(BetRamp r, double unit, double rph, double bankroll)
    {
        var c = new RampCase();
        c.Ramp = r; c.Unit = unit; c.Rph = rph; c.Bankroll = bankroll;
        return c;
    }

    static BetRamp Ramp(int min, double[] units, int[] hands)
    {
        var r = BetRamp.Create(min, min + units.Length - 1, 1);
        for (int i = 0; i < units.Length; i++)
        {
            r.Units[i] = units[i];
            if (hands != null) r.Hands[i] = hands[i];
        }
        return r;
    }

    static List<RampCase> Ramps(bool rc)
    {
        var list = new List<RampCase>();
        list.Add(Rc(BetRamp.Create(0, 0, 1), 1, 100, 1000));
        list.Add(Rc(BetRamp.Create(0, 0, 0), 25, 100, 10000));
        var two = BetRamp.Create(0, 0, 1); two.Hands[0] = 2;
        list.Add(Rc(two, 1, 100, 1000));
        if (!rc)
        {
            list.Add(Rc(Ramp(-1, new double[] { 1, 1, 2, 4, 6, 8, 10, 12 }, null), 25, 100, 25000));
            list.Add(Rc(Ramp(-2, new double[] { 0, 0, 1, 2, 4, 6, 8, 8 }, null), 10, 120, 8000));
            list.Add(Rc(Ramp(-1, new double[] { 1, 1, 1, 2, 3, 4, 4, 4 }, new[] { 1, 1, 1, 1, 2, 2, 2, 3 }), 50, 90, 40000));
            var lin = BetRamp.Create(-3, 8, 1);
            for (int v = -3; v <= 8; v++) lin.Units[v + 3] = Math.Max(0.5, 0.75 * (v + 1));
            list.Add(Rc(lin, 5, 200, 2000));
        }
        else list.Add(Rc(Ramp(-6, new double[] { 1, 1, 1, 1, 1, 2, 4, 6, 8, 8, 8 }, null), 25, 100, 10000));
        return list;
    }

    sealed class OptCase
    {
        public BetRamp Template;
        public OptimizerSettings Settings;
    }

    static OptCase Oc(BetRamp template, double min, double max, double inc, int hands, bool wong, bool kelly, double frac, double bankroll)
    {
        var o = new OptimizerSettings();
        o.MinUnits = min; o.MaxUnits = max; o.Increment = inc; o.MaxHands = hands; o.AllowWongOut = wong;
        o.Kelly = kelly; o.KellyFraction = frac; o.BankrollUnits = bankroll;
        var c = new OptCase();
        c.Template = template; c.Settings = o;
        return c;
    }

    static List<OptCase> Optimizations(bool rc)
    {
        var t = rc ? BetRamp.Create(-8, 6, 1) : BetRamp.Create(-3, 8, 1);
        var list = new List<OptCase>();
        list.Add(Oc(t, 1, 12, 1, 1, false, false, 1, 1000));
        list.Add(Oc(t, 1, 12, 1, 2, false, false, 1, 1000));
        list.Add(Oc(t, 1, 12, 1, 1, true, false, 1, 1000));
        list.Add(Oc(t, 1, 100, 1, 1, false, true, 0.5, 1000));
        if (!rc)
        {
            list.Add(Oc(t, 2, 20, 0.5, 3, false, false, 1, 1000));
            list.Add(Oc(t, 1, 50, 0.25, 1, true, true, 1, 400));
            list.Add(Oc(t, 1, 8, 0, 1, false, false, 1, 1000));
        }
        return list;
    }

    static JsonWriter AnalysisFixture()
    {
        var w = new JsonWriter();
        w.Obj();
        w.Key("sims").Arr();
        var hiLo = new CountSystem();
        var ptH = StrategyBuilder.Build(new Rules(), hiLo, new StrategyConfig());
        AnalysisCase(w, "6D H17 LS, Hi-Lo I18F4, 3 spots", RunSim(new Rules(), hiLo, ptH, 3, 2000000, 31), false);
        AnalysisCase(w, "6D H17 LS, Hi-Lo I18F4, 1 spot", RunSim(new Rules(), hiLo, ptH, 1, 2000000, 32), false);
        var ko = Preset("KO (Knock-Out)");
        var r2 = R(2, true, true, SurrenderRule.None, 4, false);
        var ptK = StrategyBuilder.Build(r2, ko, Sc(IndexMode.I18Fab4, -40, 30));
        AnalysisCase(w, "2D H17, KO I18F4, 2 spots", RunSim(r2, ko, ptK, 2, 1000000, 33), true);
        w.End();

        w.Key("phi").Arr();
        foreach (double x in new[] { -8, -6, -5.5, -5, -4.99, -3, -1.5, -0.3, 0, 0.3, 1, 2.5, 4, 7 }) w.Arr().Val(x).Val(Analysis.Phi(x)).End();
        w.End();
        w.Key("phiInv").Arr();
        foreach (double p in new[] { 0, 1e-12, 1e-6, 0.001, 0.02, 0.02425, 0.1, 0.35, 0.5, 0.77, 0.97575, 0.99, 0.999999, 1 - 1e-12, 1 })
            w.Arr().Val(p).Val(Analysis.PhiInv(p)).End();
        w.End();
        w.Key("tripRuin").Arr();
        var trips = new[]
        {
            new[] { 1, 1300, 500, 10000.0 }, new[] { 0.02, 1.3, 100, 1e5 }, new[] { 0.05, 30, 300, 2e4 }, new[] { -0.01, 1.2, 50, 1000.0 },
            new[] { 0, 1, 10, 100.0 }, new[] { 0.5, 100, 0, 10.0 }, new[] { 0.5, 100, 10, 0.0 }, new[] { 2, 1, 400, 1e6 },
            new[] { 0.01, 1.3, 20, 50.0 }, new[] { 0.001, 1, 100, 100.0 },
        };
        foreach (var t in trips) w.Arr().Val(t[0]).Val(t[1]).Val(t[2]).Val(t[3]).Val(Analysis.TripRuin(t[0], t[1], t[2], t[3])).End();
        w.End();
        w.Key("probLoss").Arr();
        foreach (var t in new[] { new[] { 0.02, 1.3, 1000.0 }, new[] { 0.1, 30, 500.0 }, new[] { -0.05, 2, 100.0 }, new[] { 0, 1, 0.0 } })
            w.Arr().Val(t[0]).Val(t[1]).Val(t[2]).Val(Analysis.ProbLoss(t[0], t[1], t[2])).End();
        w.End();
        w.End();
        return w;
    }

    static void AnalysisCase(JsonWriter w, string label, SimData d, bool rc)
    {
        w.Obj();
        w.Key("label").Val(label);
        w.Key("data"); SimDataJson(w, d);
        w.Key("bins").Arr();
        var bins = Analysis.Bins(d);
        for (int i = 0; i < bins.Length; i++)
        {
            var b = bins[i];
            if (b.N == 0) continue;
            w.Obj().Key("i").Val(i).Key("value").Val(b.Value).Key("n").Val(b.N).Key("freq").Val(b.Freq).Key("mean").Val(b.Mean)
                .Key("m2").Val(b.M2).Key("cov").Val(b.Cov).Key("measuredCov").Val(b.MeasuredCov).Key("meanVar").Val(b.MeanVar).End();
        }
        w.End();
        w.Key("evaluations").Arr();
        foreach (var e in Ramps(rc))
        {
            var res = Analysis.Evaluate(d, e.Ramp, e.Unit, e.Rph, e.Bankroll);
            w.Obj();
            w.Key("ramp"); RampJson(w, e.Ramp);
            w.Key("unit").Val(e.Unit).Key("rph").Val(e.Rph).Key("bankroll").Val(e.Bankroll);
            w.Key("result"); ResultJson(w, res);
            w.End();
        }
        w.End();
        w.Key("optimizations").Arr();
        foreach (var o in Optimizations(rc))
        {
            var opt = Analysis.Optimize(d, o.Template, o.Settings);
            w.Obj();
            w.Key("template"); RampJson(w, o.Template);
            w.Key("settings"); SettingsJson(w, o.Settings);
            w.Key("result"); RampJson(w, opt);
            w.End();
        }
        w.End();
        w.End();
    }

    static void ResultJson(JsonWriter w, AnalysisResult a)
    {
        w.Obj();
        w.Key("valid").Val(a.Valid);
        w.Key("ev").Val(a.EV).Key("var").Val(a.Var).Key("sd").Val(a.SD).Key("m2").Val(a.M2);
        w.Key("avgBet").Val(a.AvgBet).Key("playedFrac").Val(a.PlayedFrac).Key("avgBetPlayed").Val(a.AvgBetPlayed).Key("edgePct").Val(a.EdgePct);
        w.Key("di").Val(a.DI).Key("score").Val(a.Score).Key("n0").Val(a.N0).Key("seEV").Val(a.SeEV);
        w.Key("winPerHour").Val(a.WinPerHour).Key("sdPerHour").Val(a.SdPerHour).Key("winPer100").Val(a.WinPer100)
            .Key("sdPer100").Val(a.SdPer100).Key("seWinPerHour").Val(a.SeWinPerHour);
        w.Key("roundsPerHour").Val(a.RoundsPerHour).Key("unit").Val(a.Unit).Key("bankroll").Val(a.Bankroll);
        w.Key("ror").Val(a.ROR).Key("cePerHour").Val(a.CEPerHour).Key("kellyUnit").Val(a.KellyUnit).Key("hoursN0").Val(a.HoursN0).Key("spread").Val(a.Spread);
        w.Key("usedAssumedCov").Val(a.UsedAssumedCov);
        w.Key("rowFreq").Val(a.RowFreq).Key("rowEV").Val(a.RowEV).Key("rowContribution").Val(a.RowContribution).Key("rowM2").Val(a.RowM2).Key("rowN").Val(a.RowN);
        w.Key("bankrollForRor").Arr();
        foreach (double r in new[] { 0.05, 0.135, 0.01, 0.5 }) w.Val(a.BankrollForRor(r));
        w.End();
        w.Key("rorForBankroll").Arr();
        foreach (double b in new[] { 1000.0, 5000, 20000 }) w.Val(a.RorForBankroll(b));
        w.End();
        w.End();
    }

    // ---------------- JSON of engine types ----------------

    static void RulesJson(JsonWriter w, Rules r)
    {
        w.Obj().Key("decks").Val(r.Decks).Key("h17").Val(r.H17).Key("holeCard").Val(r.HoleCard.ToString())
            .Key("blackjackPays").Val(r.BlackjackPays).Key("doubleOn").Val(r.DoubleOn.ToString()).Key("das").Val(r.DAS)
            .Key("maxSplitHands").Val(r.MaxSplitHands).Key("resplitAces").Val(r.ResplitAces).Key("hitSplitAces").Val(r.HitSplitAces)
            .Key("surrender").Val(r.Surrender.ToString()).Key("insurance").Val(r.Insurance).Key("penetration").Val(r.Penetration)
            .Key("burnCard").Val(r.BurnCard).End();
    }

    static void CountJson(JsonWriter w, CountSystem cs)
    {
        w.Obj().Key("name").Val(cs.Name).Key("tags").Val(cs.Tags);
        if (cs.RedTags == null) w.Key("redTags").Null(); else w.Key("redTags").Val(cs.RedTags);
        w.Key("ircBase").Val(cs.IrcBase).Key("ircPerDeck").Val(cs.IrcPerDeck).Key("metric").Val(cs.Metric.ToString())
            .Key("deckEst").Val(cs.DeckEst.ToString()).Key("rounding").Val(cs.Rounding.ToString()).End();
    }

    static void ConfigJson(JsonWriter w, StrategyConfig sc)
    {
        w.Obj().Key("mode").Val(sc.Mode.ToString()).Key("indexMin").Val(sc.IndexMin).Key("indexMax").Val(sc.IndexMax);
        w.Key("overrides").Arr();
        foreach (var o in sc.Overrides)
            w.Obj().Key("enabled").Val(o.Enabled).Key("kind").Val(o.Kind.ToString()).Key("total").Val(o.Total).Key("upcard").Val(o.Upcard)
                .Key("play").Val(o.Play.ToString()).Key("atOrAbove").Val(o.AtOrAbove).Key("index").Val(o.Index).Key("key").Val(o.Key()).End();
        w.End();
        w.End();
    }

    static void RampJson(JsonWriter w, BetRamp r)
    {
        w.Obj().Key("min").Val(r.Min).Key("max").Val(r.Max).Key("units").Val(r.Units).Key("hands").Val(r.Hands).End();
    }

    static void SettingsJson(JsonWriter w, OptimizerSettings o)
    {
        w.Obj().Key("minUnits").Val(o.MinUnits).Key("maxUnits").Val(o.MaxUnits).Key("increment").Val(o.Increment)
            .Key("maxHands").Val(o.MaxHands).Key("allowWongOut").Val(o.AllowWongOut).Key("kelly").Val(o.Kelly)
            .Key("kellyFraction").Val(o.KellyFraction).Key("bankrollUnits").Val(o.BankrollUnits).End();
    }

    static string Hex(byte[] a, int off, int len)
    {
        var sb = new StringBuilder(len * 2);
        for (int i = 0; i < len; i++) sb.Append(a[off + i].ToString("x2", CultureInfo.InvariantCulture));
        return sb.ToString();
    }

    static string Fnv(byte[] a)
    {
        ulong h = 14695981039346656037UL;
        foreach (byte b in a) { h ^= b; h *= 1099511628211UL; }
        return h.ToString("x16", CultureInfo.InvariantCulture);
    }

    static string Fnv(short[] a)
    {
        ulong h = 14695981039346656037UL;
        foreach (short v in a) { h ^= (ushort)v; h *= 1099511628211UL; }
        return h.ToString("x16", CultureInfo.InvariantCulture);
    }
}

/// <summary>Minimal JSON writer: doubles round-trip (G17), NaN is null and infinities are the strings "Infinity"/"-Infinity".</summary>
sealed class JsonWriter
{
    readonly StringBuilder sb = new StringBuilder(1 << 20);
    readonly Stack<char> closers = new Stack<char>();
    readonly Stack<bool> empty = new Stack<bool>();
    bool afterKey;

    /// <summary>Writes the separator before a value; returns true when the value follows a key.</summary>
    bool Sep()
    {
        if (afterKey) { afterKey = false; return true; }
        if (empty.Count > 0)
        {
            if (empty.Peek()) { empty.Pop(); empty.Push(false); }
            else sb.Append(',');
        }
        return false;
    }

    void NewLine() { sb.Append('\n').Append(' ', 2 * closers.Count); }

    void Open(char open, char close)
    {
        bool keyed = Sep();
        if (!keyed && open == '{' && closers.Count > 0 && closers.Count <= 2) NewLine();
        sb.Append(open);
        closers.Push(close);
        empty.Push(true);
    }

    public JsonWriter Obj() { Open('{', '}'); return this; }
    public JsonWriter Arr() { Open('[', ']'); return this; }
    public JsonWriter End() { empty.Pop(); sb.Append(closers.Pop()); return this; }

    public JsonWriter Key(string k)
    {
        Sep();
        if (closers.Count <= 3) NewLine();
        Quote(k);
        sb.Append(':');
        afterKey = true;
        return this;
    }

    public JsonWriter Null() { Sep(); sb.Append("null"); return this; }
    public JsonWriter Val(bool v) { Sep(); sb.Append(v ? "true" : "false"); return this; }
    public JsonWriter Val(int v) { Sep(); sb.Append(v.ToString(CultureInfo.InvariantCulture)); return this; }
    public JsonWriter Val(long v) { Sep(); sb.Append(v.ToString(CultureInfo.InvariantCulture)); return this; }
    public JsonWriter Val(double v) { Sep(); sb.Append(Num(v)); return this; }
    public JsonWriter Val(string v) { Sep(); if (v == null) sb.Append("null"); else Quote(v); return this; }

    public JsonWriter Val(double[] a) { Arr(); foreach (double v in a) Val(v); return End(); }
    public JsonWriter Val(int[] a) { Arr(); foreach (int v in a) Val(v); return End(); }
    public JsonWriter Val(long[] a) { Arr(); foreach (long v in a) Val(v); return End(); }

    static string Num(double v)
    {
        if (double.IsNaN(v)) return "null";
        if (double.IsPositiveInfinity(v)) return "\"Infinity\"";
        if (double.IsNegativeInfinity(v)) return "\"-Infinity\"";
        return v.ToString("G17", CultureInfo.InvariantCulture);
    }

    void Quote(string s)
    {
        sb.Append('"');
        foreach (char c in s)
        {
            if (c == '"' || c == '\\') sb.Append('\\').Append(c);
            else if (c < ' ') sb.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
            else sb.Append(c);
        }
        sb.Append('"');
    }

    public override string ToString() { return sb.ToString(); }
}
