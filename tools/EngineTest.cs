using System;
using System.Diagnostics;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using CalcBJ.Engine;

static class EngineTest
{
    static int failures;

    /// <summary>Prints a failed check; the exit code is 1 when any check failed.</summary>
    static void Check(bool ok, string what)
    {
        if (ok) return;
        failures++;
        Console.WriteLine("FAIL: " + what);
    }

    static double HE(string label, Rules r)
    {
        var sw = Stopwatch.StartNew();
        double ev = CAEngine.HouseEdgeOffTop(r);
        Console.WriteLine("{0,-44} player EV {1,8:0.0000}%   ({2} ms)", label, ev * 100, sw.ElapsedMilliseconds);
        return ev;
    }

    /// <summary>A house edge against a published figure (they differ in the second decimal with the split rules).</summary>
    static void Published(string label, Rules r, double expectedPct)
    {
        double ev = HE(label, r) * 100;
        Check(Math.Abs(ev - expectedPct) <= 0.03, string.Format(CultureInfo.InvariantCulture, "{0}: {1:0.000}% vs published {2:0.00}%", label, ev, expectedPct));
    }

    /// <summary>A basic-strategy chart code against the exact play (CAEngine.BasicStrategy; also the published chart).</summary>
    static void Play(string label, Rules r, char kind, int total, int up, string expect)
    {
        var sc = new StrategyConfig(); sc.Mode = IndexMode.BasicOnly;
        var pt = StrategyBuilder.Build(r, new CountSystem(), sc);
        int cell = kind == 'P' ? PlayTable.PairCell(total, up) : (kind == 'S' ? PlayTable.SoftCell(total, up) : PlayTable.HardCell(total, up));
        string got = StrategyBuilder.ActionCode(pt.Basic[cell], kind == 'P');
        Console.WriteLine("{0,-26} {1}{2,-2} v {3,-2} {4,-3} (exact {5})", label, kind, total, up, got, expect);
        Check(got == expect, label + " " + kind + total + " v " + up + ": " + got + ", exact play " + expect);
    }

    /// <summary>
    /// Basic strategy must be the best there is. Every decision of every chart cell (stand or hit, double, surrender,
    /// split) is changed in turn and the changed strategy's exact off-the-top expectation worked out: none may gain
    /// more than 0.0001%. A wrong cell fails this even when it costs 0.001%, which the published house edges and the
    /// simulations can't see. Two known leftovers stay below the limit (found in 77 of 600 rule sets, nothing else in
    /// any of them): with two decks and DAS, doubling soft 17 v 2 gains up to 0.00006%, because the hands made by
    /// splitting sixes use that cell too; with three decks and surrender, hitting 16 v 9 gains 0.00007%, because the
    /// cell's average still counts the pair of eights, which is split.
    /// </summary>
    static void BestPlay(string label, Rules r)
    {
        var sw = Stopwatch.StartNew();
        ExactAdjust adj; double offTop;
        CAResult basic = CAEngine.BasicFor(r, out adj, out offTop);
        byte[] bits = { Flag.Stand, Flag.Double, Flag.Surrender, Flag.Split };
        string[] names = { "stand/hit", "double", "surrender", "split" };
        var gains = new double[PlayTable.CellsPerBin * 4];
        Parallel.For(0, gains.Length, () => new CAEngine(r), (i, state, eng) =>
        {
            int cell = i / 4; byte bit = bits[i % 4];
            // The split bit belongs to the pair cells, and only where the rules let the player split.
            if (bit == Flag.Split && (cell < PlayTable.PairCell(1, 1) || r.MaxSplitHands < 2)) return eng;
            var alt = new CAResult();
            alt.Flags = (byte[])basic.Flags.Clone();
            alt.Flags[cell] ^= bit;
            alt.Insure = basic.Insure; alt.InsuranceEV = basic.InsuranceEV;
            gains[i] = eng.ExactOffTop(alt) - offTop;
            return eng;
        }, eng => { });
        int changed = 0, at = 0;
        for (int i = 0; i < gains.Length; i++)
        {
            if (gains[i] != 0) changed++;
            if (gains[i] > gains[at]) at = i;
        }
        int c = at / 4, u = c % 10 + 1;
        string where = (c >= 440 ? "pair of " + ((c - 440) / 10 + 1) : (c >= 220 ? "soft " + (c - 220) / 10 : "hard " + c / 10)) + " v " + u + ", " + names[at % 4];
        Console.WriteLine("{0,-28} player EV {1,8:0.0000}%   {2} changed decisions tried, best gain {3:0.0000000}%{4}   ({5} ms)",
            label, offTop * 100, changed, Math.Max(0, gains[at]) * 100, gains[at] > 0 ? " (" + where + ")" : "", sw.ElapsedMilliseconds);
        Check(changed > 300, label + ": only " + changed + " changed decisions had an effect");
        Check(gains[at] <= 1e-6, string.Format(CultureInfo.InvariantCulture, "{0}: {1} gains {2:0.000000}%", label, where, gains[at] * 100));
    }

    static Rules R(int decks, bool h17, bool das, SurrenderRule sur, int maxSplit, bool rsa)
    {
        var r = new Rules();
        r.Decks = decks; r.H17 = h17; r.DAS = das; r.Surrender = sur; r.MaxSplitHands = maxSplit; r.ResplitAces = rsa;
        return r;
    }

    static int Main(string[] args)
    {
        Thread.CurrentThread.CurrentCulture = CultureInfo.InvariantCulture;
        string mode = args.Length > 0 ? args[0] : "ca";
        if (mode == "ca" || mode == "all")
        {
            Published("8D S17 DAS noSurr SP4", R(8, false, true, SurrenderRule.None, 4, false), -0.43);
            Published("6D S17 DAS noSurr SP4", R(6, false, true, SurrenderRule.None, 4, false), -0.40);
            Published("6D H17 DAS noSurr SP4", R(6, true, true, SurrenderRule.None, 4, false), -0.62);
            HE("6D H17 DAS LS SP4", R(6, true, true, SurrenderRule.Late, 4, false));
            HE("6D S17 DAS LS SP4 RSA", R(6, false, true, SurrenderRule.Late, 4, true));
            HE("2D H17 DAS noSurr SP4", R(2, true, true, SurrenderRule.None, 4, false));
            HE("2D S17 DAS noSurr SP4", R(2, false, true, SurrenderRule.None, 4, false));
            HE("1D H17 noDAS noSurr SP2", R(1, true, false, SurrenderRule.None, 2, false));
            HE("1D S17 noDAS noSurr SP2", R(1, false, false, SurrenderRule.None, 2, false));
            var enhc = R(6, false, true, SurrenderRule.None, 4, false); enhc.HoleCard = HoleCardRule.ENHC;
            HE("6D S17 DAS ENHC", enhc);
            var r65 = R(6, true, true, SurrenderRule.None, 4, false); r65.BlackjackPays = 1.2;
            HE("6D H17 DAS 6:5", r65);
            var es = R(6, false, true, SurrenderRule.Early, 4, false);
            HE("6D S17 DAS ES", es);
            for (int u = 1; u <= 10; u++) Console.Write("dag{0}={1} ", u, DealerDag_Count(u));
            Console.WriteLine();

            // Plays the analysis's fixed draw odds got wrong with one or two decks, and 16 v T with surrender (hands of
            // three or more cards stand). The exact plays come from an independent exact calculation.
            Play("2D S17 DAS", R(2, false, true, SurrenderRule.None, 4, false), 'H', 11, 1, "D");
            Play("1D S17 DAS", R(1, false, true, SurrenderRule.None, 4, false), 'S', 13, 4, "D");
            Play("1D S17 DAS", R(1, false, true, SurrenderRule.None, 4, false), 'S', 19, 6, "Ds");
            Play("1D S17 DAS LS", R(1, false, true, SurrenderRule.Late, 4, false), 'H', 16, 1, "R");
            Play("2D H17 DAS", R(2, true, true, SurrenderRule.None, 4, false), 'S', 18, 2, "Ds");
            Play("2D S17 DAS", R(2, false, true, SurrenderRule.None, 4, false), 'P', 6, 7, "P");
            Play("2D S17 DAS", R(2, false, true, SurrenderRule.None, 4, false), 'P', 7, 8, "P");
            Play("1D H17 DAS", R(1, true, true, SurrenderRule.None, 4, false), 'P', 9, 1, "P");
            Play("1D H17 noDAS SP2", R(1, true, false, SurrenderRule.None, 2, false), 'P', 2, 3, "P");
            Play("1D H17 DAS LS", R(1, true, true, SurrenderRule.Late, 4, false), 'H', 16, 10, "Rs");
            Play("6D H17 DAS LS", R(6, true, true, SurrenderRule.Late, 4, false), 'H', 16, 10, "Rs");
            Play("6D S17 DAS ES", R(6, false, true, SurrenderRule.Early, 4, false), 'H', 16, 10, "Rs");
            Play("6D H17 DAS noSurr", R(6, true, true, SurrenderRule.None, 4, false), 'H', 16, 10, "H");
        }
        if (mode == "best" || mode == "all")
        {
            BestPlay("6D H17 DAS LS", R(6, true, true, SurrenderRule.Late, 4, false));
            BestPlay("6D S17 DAS noSurr", R(6, false, true, SurrenderRule.None, 4, false));
            BestPlay("2D H17 DAS noSurr", R(2, true, true, SurrenderRule.None, 4, false));
            BestPlay("2D S17 DAS noSurr", R(2, false, true, SurrenderRule.None, 4, false));
            BestPlay("1D S17 noDAS SP2", R(1, false, false, SurrenderRule.None, 2, false));
            BestPlay("1D H17 DAS LS", R(1, true, true, SurrenderRule.Late, 4, false));
            BestPlay("1D S17 DAS LS", R(1, false, true, SurrenderRule.Late, 4, false));
            var bestEnhc = R(8, false, true, SurrenderRule.None, 4, false); bestEnhc.HoleCard = HoleCardRule.ENHC;
            BestPlay("8D S17 DAS ENHC", bestEnhc);
            var bestEs = R(6, false, true, SurrenderRule.Early, 3, true); bestEs.HitSplitAces = true;
            BestPlay("6D S17 DAS ES RSA HSA SP3", bestEs);
            var bestD10 = R(2, false, false, SurrenderRule.None, 4, false); bestD10.DoubleOn = DoubleRule.TenToEleven; bestD10.BlackjackPays = 1.2;
            BestPlay("2D S17 noDAS D10 6:5", bestD10);
            var bestObo = R(4, true, true, SurrenderRule.Late, 4, false); bestObo.HoleCard = HoleCardRule.ENHC_OBO;
            BestPlay("4D H17 DAS LS OBO", bestObo);
            BestPlay("3D S17 DAS no splitting", R(3, false, true, SurrenderRule.None, 1, false));
        }
        if (mode == "idx" || mode == "all")
        {
            var r = R(6, false, true, SurrenderRule.Late, 4, false);
            var cs = new CountSystem();
            var sc = new StrategyConfig(); sc.Mode = IndexMode.FullIndices; sc.IndexMin = -10; sc.IndexMax = 10;
            var sw = Stopwatch.StartNew();
            var pt = StrategyBuilder.Build(r, cs, sc);
            Console.WriteLine("Build full indices: {0} ms", sw.ElapsedMilliseconds);
            foreach (var info in StrategyBuilder.DescribeIndices(pt, -10, 10))
                Console.WriteLine("  {0,-12} {1}", info.Hand, info.Text);
            for (int v = -6; v <= 8; v++)
                Console.WriteLine("  TC {0,3}: tcEq {1,6:0.00}", v, pt.BinTcEq[v - PlayTable.BinMin]);
        }
        if (mode == "offtop")
        {
            // 2 billion rounds per game (about 15 s each) make one standard error 0.0026%: enough to catch the 0.01-0.02%
            // the split approximation used to cost with one or two decks.
            long rounds = args.Length > 1 ? long.Parse(args[1]) : 2000000000;
            var list = new System.Collections.Generic.List<System.Tuple<string, Rules>>();
            list.Add(System.Tuple.Create("6D S17 DAS LS SP4", R(6, false, true, SurrenderRule.Late, 4, false)));
            list.Add(System.Tuple.Create("6D H17 DAS noSurr SP4", R(6, true, true, SurrenderRule.None, 4, false)));
            list.Add(System.Tuple.Create("1D H17 noDAS noSurr SP2", R(1, true, false, SurrenderRule.None, 2, false)));
            list.Add(System.Tuple.Create("1D S17 DAS SP4 RSA", R(1, false, true, SurrenderRule.None, 4, true)));
            var hsa1 = R(1, false, true, SurrenderRule.None, 4, true); hsa1.HitSplitAces = true;
            list.Add(System.Tuple.Create("1D S17 DAS SP4 RSA HSA", hsa1));
            list.Add(System.Tuple.Create("2D H17 DAS LS SP4", R(2, true, true, SurrenderRule.Late, 4, false)));
            list.Add(System.Tuple.Create("2D S17 DAS LS SP4 RSA", R(2, false, true, SurrenderRule.Late, 4, true)));
            var e1 = R(6, false, true, SurrenderRule.None, 4, false); e1.HoleCard = HoleCardRule.ENHC;
            list.Add(System.Tuple.Create("6D S17 DAS ENHC", e1));
            var e2 = R(8, false, true, SurrenderRule.Early, 4, false);
            list.Add(System.Tuple.Create("8D S17 DAS ES", e2));
            var e3 = R(6, true, true, SurrenderRule.None, 4, false); e3.BlackjackPays = 1.2; e3.DoubleOn = DoubleRule.TenToEleven;
            list.Add(System.Tuple.Create("6D H17 DAS 6:5 D10-11", e3));
            var e4 = R(4, false, true, SurrenderRule.None, 4, true); e4.HitSplitAces = true; e4.HoleCard = HoleCardRule.ENHC_OBO;
            list.Add(System.Tuple.Create("4D S17 DAS RSA HSA OBO", e4));
            foreach (var t in list)
            {
                var r = t.Item2; r.Penetration = 0;
                var cs = new CountSystem();
                var sc = new StrategyConfig(); sc.Mode = IndexMode.BasicOnly;
                var pt = StrategyBuilder.Build(r, cs, sc);
                var su = new SimSetup();
                su.Rules = r; su.Count = cs; su.Table = pt; su.Spots = 1; su.Rounds = rounds;
                su.Threads = Environment.ProcessorCount; su.Seed = 777;
                var sim = new Simulator(su);
                var done = new ManualResetEvent(false);
                sim.Finished += (s, e) => done.Set();
                sim.Start(); done.WaitOne();
                if (sim.Error != null) { Console.WriteLine(sim.Error); return 1; }
                var d = sim.Snapshot();
                var flat = Analysis.Evaluate(d, BetRamp.Create(0, 0, 1), 1, 100, 1000);
                double ca = pt.BasicResult.PlayerEV;
                double z = (flat.EV - ca) / flat.SeEV;
                Console.WriteLine("{0,-26} sim {1,8:0.0000}% +- {2:0.0000}%   CA {3,8:0.0000}%   diff {4,7:0.0000}% ({5:0.0} SE)  {6:0.0}M/s",
                    t.Item1, flat.EV * 100, flat.SeEV * 100, ca * 100, (flat.EV - ca) * 100, z, d.Rounds / d.Seconds / 1e6);
                Check(Math.Abs(z) <= 3.5, t.Item1 + ": simulation and analysis differ by " + z.ToString("0.0", CultureInfo.InvariantCulture) + " SE");
            }
        }
        if (mode == "regress" || mode == "all")
        {
            // The composition at a count (index generation): how much each rank's expected number of unseen cards moves
            // per unit of their tag sum must match random draws of unseen cards, red and black tags included.
            var presets = CountSystem.Presets();
            var colours = presets[0].Clone();
            colours.Name = "Hi-Lo with red 7 +1 / black 7 -1, red 9 -1 / black 9 +1";
            colours.Tags = new double[] { -1, 1, 1, 1, 1, 1, -1, 0, 1, -1 };
            colours.RedTags = new double[] { -1, 1, 1, 1, 1, 1, 1, 0, -1, -1 };
            foreach (var cs in new[] { presets[0], presets[5], presets[11], colours })
                foreach (int decks in new[] { 1, 6 })
                    RegressCheck(cs, decks, decks == 1 ? 2000000 : 400000);
        }
        if (mode == "bench")
        {
            // Speed (all threads) plus a single-thread checksum that must not change when the hot loop is optimised.
            int threads = args.Length > 1 ? int.Parse(args[1]) : Environment.ProcessorCount;
            var h17 = R(6, true, true, SurrenderRule.Late, 4, false);
            var cs = new CountSystem();
            var i18 = new StrategyConfig(); i18.Mode = IndexMode.I18Fab4;
            var full = new StrategyConfig(); full.Mode = IndexMode.FullIndices;
            var ko = CountSystem.Presets()[10];
            var koSc = new StrategyConfig(); koSc.Mode = IndexMode.I18Fab4; koSc.IndexMin = -40; koSc.IndexMax = 30;
            var d2 = R(2, true, true, SurrenderRule.None, 4, false);
            var d1 = R(1, true, false, SurrenderRule.None, 2, false);
            Bench("6D H17 LS Hi-Lo I18F4, 1 spot", h17, cs, i18, 1, 300000000, threads);
            Bench("6D H17 LS Hi-Lo I18F4, 3 spots", h17, cs, i18, 3, 200000000, threads);
            Bench("6D H17 LS Hi-Lo I18F4, 7 spots", h17, cs, i18, 7, 100000000, threads);
            Bench("6D H17 LS Hi-Lo full, 3 spots", h17, cs, full, 3, 200000000, threads);
            Bench("2D H17 KO RC I18F4, 2 spots", d2, ko, koSc, 2, 200000000, threads);
            Bench("1D H17 Hi-Lo full, 1 spot", d1, cs, full, 1, 300000000, threads);
        }
        if (mode == "misc")
        {
            // (a) ENHC + late surrender, reshuffle every round: sim vs CA
            var r = R(6, false, true, SurrenderRule.Late, 4, false); r.HoleCard = HoleCardRule.ENHC; r.Penetration = 0;
            var cs = new CountSystem();
            var sc = new StrategyConfig(); sc.Mode = IndexMode.BasicOnly;
            var pt = StrategyBuilder.Build(r, cs, sc);
            var d = Run(r, cs, pt, 1, 200000000);
            var flat = Analysis.Evaluate(d, BetRamp.Create(0, 0, 1), 1, 100, 1000);
            Console.WriteLine("ENHC+LS off-top: sim {0:0.0000}% +- {1:0.0000}%  CA {2:0.0000}%", flat.EV * 100, flat.SeEV * 100, pt.BasicResult.PlayerEV * 100);

            // (b) KO running count, 6D S17 DAS LS, 2 spots
            r = R(6, false, true, SurrenderRule.Late, 4, false);
            var ko = CountSystem.Presets()[10];
            Console.WriteLine("KO: {0}, IRC {1}, metric {2}", ko.Name, ko.Irc(6), ko.Metric);
            var sk = new StrategyConfig(); sk.Mode = IndexMode.I18Fab4; sk.IndexMin = -30; sk.IndexMax = 30;
            var pk = StrategyBuilder.Build(r, ko, sk);
            var dk = Run(r, ko, pk, 2, 100000000);
            var bins = Analysis.Bins(dk);
            for (int v = -24; v <= 12; v += 2)
            {
                var b = bins[v - PlayTable.BinMin];
                Console.WriteLine("  RC {0,4}: freq {1,6:0.00}%  EV {2,7:0.000}%  tcEq {3,6:0.00}", v, b.Freq * 100, b.Mean * 100, StrategyBuilder.TcEquivalent(ko, r, v));
            }
            foreach (var info in StrategyBuilder.DescribeIndices(pk, -30, 30))
                if (info.Hand.StartsWith("16 v T") || info.Hand.StartsWith("Insurance") || info.Hand.StartsWith("12 v 3") || info.Hand.StartsWith("10 v T"))
                    Console.WriteLine("  KO index {0,-10} {1}", info.Hand, info.Text);

            // (c) optimiser on Hi-Lo 6D S17 DAS LS, 3 spots
            var sh = new StrategyConfig(); sh.Mode = IndexMode.I18Fab4;
            var ph = StrategyBuilder.Build(r, cs, sh);
            var dh = Run(r, cs, ph, 3, 200000000);
            var tmpl = BetRamp.Create(-3, 8, 1);
            var os = new OptimizerSettings(); os.MinUnits = 1; os.MaxUnits = 12; os.Increment = 1; os.MaxHands = 1;
            var opt = Analysis.Optimize(dh, tmpl, os);
            var ao = Analysis.Evaluate(dh, opt, 25, 100, 25000);
            Console.Write("Optimal 1-12 (SCORE): ");
            for (int i = 0; i < opt.Rows; i++) Console.Write("{0}:{1}{2} ", opt.Min + i, opt.Units[i], opt.Hands[i] > 1 ? "x" + opt.Hands[i] : "");
            Console.WriteLine();
            Console.WriteLine("  SCORE {0:0.00} DI {1:0.00} WR ${2:0.00}/h", ao.Score, ao.DI, ao.WinPerHour);
            os.MaxHands = 2;
            opt = Analysis.Optimize(dh, tmpl, os);
            ao = Analysis.Evaluate(dh, opt, 25, 100, 25000);
            Console.Write("Optimal 1-12 x2 hands: ");
            for (int i = 0; i < opt.Rows; i++) Console.Write("{0}:{1}{2} ", opt.Min + i, opt.Units[i], opt.Hands[i] > 1 ? "x" + opt.Hands[i] : "");
            Console.WriteLine();
            Console.WriteLine("  SCORE {0:0.00} DI {1:0.00} WR ${2:0.00}/h", ao.Score, ao.DI, ao.WinPerHour);
            os.MaxHands = 1; os.AllowWongOut = true;
            opt = Analysis.Optimize(dh, tmpl, os);
            ao = Analysis.Evaluate(dh, opt, 25, 100, 25000);
            Console.Write("Optimal wong-out: ");
            for (int i = 0; i < opt.Rows; i++) Console.Write("{0}:{1} ", opt.Min + i, opt.Units[i]);
            Console.WriteLine();
            Console.WriteLine("  SCORE {0:0.00} DI {1:0.00} WR ${2:0.00}/h played {3:0.0}%", ao.Score, ao.DI, ao.WinPerHour, ao.PlayedFrac * 100);
            os.AllowWongOut = false; os.Kelly = true; os.BankrollUnits = 1000; os.KellyFraction = 0.5; os.MaxUnits = 100;
            opt = Analysis.Optimize(dh, tmpl, os);
            ao = Analysis.Evaluate(dh, opt, 25, 100, 25000);
            Console.Write("Half-Kelly (1000u): ");
            for (int i = 0; i < opt.Rows; i++) Console.Write("{0}:{1} ", opt.Min + i, opt.Units[i]);
            Console.WriteLine();
            Console.WriteLine("  SCORE {0:0.00} ROR {1:0.000}% WR ${2:0.00}/h", ao.Score, ao.ROR * 100, ao.WinPerHour);

            // (d) two hands flat: variance check
            var two = BetRamp.Create(0, 0, 1); two.Hands[0] = 2;
            var a2 = Analysis.Evaluate(dh, two, 1, 100, 1000);
            var a1 = Analysis.Evaluate(dh, BetRamp.Create(0, 0, 1), 1, 100, 1000);
            Console.WriteLine("Flat 1 hand: EV {0:0.0000} var {1:0.0000};  2 hands: EV {2:0.0000} var {3:0.0000} (ratio {4:0.000})", a1.EV, a1.Var, a2.EV, a2.Var, a2.Var / a1.Var);
            Console.WriteLine("Trip ruin test: mu=1, var=1300, B=500, n=10000 -> {0:0.0000}; lifetime {1:0.0000}", Analysis.TripRuin(1, 1300, 500, 10000), Math.Exp(-2 * 500.0 / 1300));
        }
        if (mode == "sim" || mode == "all")
        {
            long rounds = args.Length > 1 ? long.Parse(args[1]) : 50000000;
            int spots = args.Length > 2 ? int.Parse(args[2]) : 1;
            string idx = args.Length > 3 ? args[3] : "basic";
            var r = R(6, false, true, SurrenderRule.Late, 4, false);
            var cs = new CountSystem();
            var sc = new StrategyConfig();
            sc.Mode = idx == "basic" ? IndexMode.BasicOnly : (idx == "full" ? IndexMode.FullIndices : IndexMode.I18Fab4);
            var pt = StrategyBuilder.Build(r, cs, sc);
            var su = new SimSetup();
            su.Rules = r; su.Count = cs; su.Table = pt; su.Spots = spots; su.Rounds = rounds;
            su.Threads = Environment.ProcessorCount; su.Seed = 12345;
            var sim = new Simulator(su);
            var done = new ManualResetEvent(false);
            sim.Finished += (s, e) => done.Set();
            sim.Start();
            done.WaitOne();
            if (sim.Error != null) { Console.WriteLine(sim.Error); return 1; }
            var d = sim.Snapshot();
            Console.WriteLine("Rounds {0:N0} in {1:0.00}s = {2:0.0}M rounds/s, shoes {3:N0}, emergency {4}", d.Rounds, d.Seconds, d.Rounds / d.Seconds / 1e6, d.Shoes, d.Emergency);
            var ramp = BetRamp.Create(-1, 1, 1);
            var flat = Analysis.Evaluate(d, ramp, 1, 100, 1000);
            Console.WriteLine("Flat bet EV {0:0.0000}% +- {1:0.0000}%   SD/hand {2:0.0000}   CA off-top {3:0.0000}%", flat.EV * 100, flat.SeEV * 100, flat.SD, pt.BasicResult.PlayerEV * 100);
            var bins = Analysis.Bins(d);
            for (int v = -6; v <= 8; v++)
            {
                var b = bins[v - PlayTable.BinMin];
                Console.WriteLine("  TC {0,3}: freq {1,6:0.00}%  EV {2,7:0.000}%  SD {3:0.000}  cov {4:0.000}", v, b.Freq * 100, b.Mean * 100, Math.Sqrt(b.Var), b.Cov - b.Mean * b.Mean);
            }
            var r12 = BetRamp.Create(-1, 6, 1);
            double[] u = { 1, 1, 2, 4, 6, 8, 10, 12 };
            for (int i = 0; i < u.Length; i++) r12.Units[i] = u[i];
            var a = Analysis.Evaluate(d, r12, 25, 100, 25000);
            Console.WriteLine("1-12 ramp: WR ${0:0.00}/h  SD ${1:0.00}/h  edge {2:0.000}%  DI {3:0.00}  SCORE {4:0.00}  N0 {5:N0}  ROR {6:0.00}%",
                a.WinPerHour, a.SdPerHour, a.EdgePct, a.DI, a.Score, a.N0, a.ROR * 100);
        }
        if (failures > 0) Console.WriteLine(failures + " check(s) failed.");
        return failures > 0 ? 1 : 0;
    }

    static void Bench(string label, Rules r, CountSystem cs, StrategyConfig sc, int spots, long rounds, int threads)
    {
        var pt = StrategyBuilder.Build(r, cs, sc);
        // deterministic single-thread checksum
        var su = new SimSetup();
        su.Rules = r; su.Count = cs; su.Table = pt; su.Spots = spots; su.Rounds = 4000000; su.Threads = 1; su.Seed = 99;
        var d1 = RunSetup(su);
        long sum = 0;
        for (int i = 0; i < d1.NumBins; i++) sum = sum * 31 + d1.N[i] * 7 + d1.SA[i] * 3 + d1.QA[i] + d1.AB[i];
        double rate1 = d1.Rounds / d1.Seconds / 1e6;
        su.Rounds = rounds; su.Threads = threads; su.Seed = 1234;
        var d = RunSetup(su);
        Console.WriteLine("{0,-34} 1 thread {1,6:0.00}M/s   {2} threads {3,6:0.0}M/s   checksum {4:X16}", label, rate1, threads, d.Rounds / d.Seconds / 1e6, sum);
    }

    static SimData RunSetup(SimSetup su)
    {
        var sim = new Simulator(su);
        var done = new ManualResetEvent(false);
        sim.Finished += (s, e) => done.Set();
        sim.Start(); done.WaitOne();
        if (sim.Error != null) throw sim.Error;
        return sim.Snapshot();
    }

    static SimData Run(Rules r, CountSystem cs, PlayTable pt, int spots, long rounds)
    {
        var su = new SimSetup();
        su.Rules = r; su.Count = cs; su.Table = pt; su.Spots = spots; su.Rounds = rounds;
        su.Threads = Environment.ProcessorCount; su.Seed = 4242;
        var sim = new Simulator(su);
        var done = new ManualResetEvent(false);
        sim.Finished += (s, e) => done.Set();
        sim.Start(); done.WaitOne();
        if (sim.Error != null) throw sim.Error;
        return sim.Snapshot();
    }

    /// <summary>
    /// Draws random sets of unseen cards from a shoe without a ten-ten-six deal and compares the least-squares slope of
    /// each rank's count on the set's tag sum with the slope of the count model (CAEngine.Regress, the composition used
    /// for index plays). Tens and sixes count the same in red and black in every tested count, so the colours of the
    /// population stay even, as the model assumes.
    /// </summary>
    static void RegressCheck(CountSystem cs, int decks, int samples)
    {
        var codes = new System.Collections.Generic.List<int>();
        var left = new int[20];
        for (int code = 0; code < 20; code++) left[code] = decks * (code >= 18 ? 8 : 2);
        left[18]--; left[19]--; left[10]--;   // upcard ten, player's ten and six
        for (int code = 0; code < 20; code++) for (int k = 0; k < left[code]; k++) codes.Add(code);
        int[] deck = codes.ToArray();
        int m = deck.Length, n = m / 2;
        var popl = new double[11];
        double tagSum = 0;
        foreach (int code in deck) { popl[(code >> 1) + 1]++; tagSum += cs.TagOfCode(code); }

        var model = new CountModel();
        model.Tags = new double[11];
        for (int r = 1; r <= 10; r++) model.Tags[r] = cs.RankTag(r);
        if (cs.RedTags != null)
        {
            model.TagVar = new double[11];
            for (int r = 1; r <= 10; r++) { double h = 0.5 * (cs.RedTags[r - 1] - cs.Tags[r - 1]); model.TagVar[r] = h * h; }
        }
        model.Unseen = n;
        double s0 = tagSum * n / m;
        var slope = ModelSlope(popl, model, s0);
        double[] oldSlope = null;
        if (model.TagVar != null)
        {
            var tv = model.TagVar;
            model.TagVar = null;   // the earlier model: the variance of the ranks' average tags only
            oldSlope = ModelSlope(popl, model, s0);
            model.TagVar = tv;
        }

        // Sampled slope of each rank's count on the tag sum, around the population means.
        double sx = 0, sxx = 0;
        var sy = new double[11]; var sxy = new double[11]; var syy = new double[11];
        var cnt = new int[11];
        ulong state = 0x2545F4914F6CDD1DUL ^ (ulong)(decks * 131 + cs.Name.Length);
        for (int i = 0; i < samples; i++)
        {
            Array.Clear(cnt, 0, 11);
            double s = 0;
            for (int k = 0; k < n; k++)
            {
                state += 0x9E3779B97F4A7C15UL;
                ulong z = state;
                z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
                z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
                z ^= z >> 31;
                int j = k + (int)((z >> 32) * (ulong)(m - k) >> 32);
                int c = deck[j]; deck[j] = deck[k]; deck[k] = c;
                s += cs.TagOfCode(c);
                cnt[(c >> 1) + 1]++;
            }
            double x = s - s0;
            sx += x; sxx += x * x;
            for (int r = 1; r <= 10; r++)
            {
                double y = cnt[r] - popl[r] * n / m;
                sy[r] += y; sxy[r] += x * y; syy[r] += y * y;
            }
        }
        double cxx = sxx - sx * sx / samples;
        double worst = 0, worstOld = 0;
        for (int r = 1; r <= 10; r++)
        {
            double cxy = sxy[r] - sx * sy[r] / samples, cyy = syy[r] - sy[r] * sy[r] / samples;
            double b = cxy / cxx;
            double se = Math.Sqrt(Math.Max(1e-30, (cyy - b * cxy) / (samples - 2) / cxx));
            worst = Math.Max(worst, Math.Abs(slope[r] - b) / se);
            if (oldSlope != null) worstOld = Math.Max(worstOld, Math.Abs(oldSlope[r] - b) / se);
        }
        Console.WriteLine("{0,-58} {1}D: largest slope difference {2,4:0.0} SE{3}", cs.Name, decks, worst,
            oldSlope != null ? string.Format(CultureInfo.InvariantCulture, " (without the red/black spread {0:0.0} SE)", worstOld) : "");
        Check(worst <= 4, cs.Name + " " + decks + "D: the count model's composition differs from random draws by " + worst.ToString("0.0", CultureInfo.InvariantCulture) + " SE");
    }

    /// <summary>The model's change in each rank's unseen cards per unit of their tag sum, around s0.</summary>
    static double[] ModelSlope(double[] popl, CountModel model, double s0)
    {
        var regress = typeof(CAEngine).GetMethod("Regress", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
        var lo = new double[11];
        var hi = new double[11];
        model.UnseenTags = s0 - 0.5;
        regress.Invoke(null, new object[] { popl, model, lo });
        model.UnseenTags = s0 + 0.5;
        regress.Invoke(null, new object[] { popl, model, hi });
        var slope = new double[11];
        for (int r = 1; r <= 10; r++) slope[r] = hi[r] - lo[r];
        return slope;
    }

    static int DealerDag_Count(int u)
    {
        var t = typeof(CAEngine).Assembly.GetType("CalcBJ.Engine.DealerDag");
        var m = t.GetMethod("Get", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);
        object dag = m.Invoke(null, new object[] { u, false });
        return (int)t.GetField("Count").GetValue(dag);
    }
}
