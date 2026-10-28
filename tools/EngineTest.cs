using System;
using System.Diagnostics;
using System.Globalization;
using System.Threading;
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
                Console.WriteLine("  TC {0,3}: tcEq {1,6:0.00}  CA EV {2,7:0.000}%", v, pt.BinTcEq[v - PlayTable.BinMin], pt.BinEV[v - PlayTable.BinMin] * 100);
        }
        if (mode == "offtop")
        {
            long rounds = args.Length > 1 ? long.Parse(args[1]) : 200000000;
            var list = new System.Collections.Generic.List<System.Tuple<string, Rules>>();
            list.Add(System.Tuple.Create("6D S17 DAS LS SP4", R(6, false, true, SurrenderRule.Late, 4, false)));
            list.Add(System.Tuple.Create("6D H17 DAS noSurr SP4", R(6, true, true, SurrenderRule.None, 4, false)));
            list.Add(System.Tuple.Create("1D H17 noDAS noSurr SP2", R(1, true, false, SurrenderRule.None, 2, false)));
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

    static int DealerDag_Count(int u)
    {
        var t = typeof(CAEngine).Assembly.GetType("CalcBJ.Engine.DealerDag");
        var m = t.GetMethod("Get", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);
        object dag = m.Invoke(null, new object[] { u, false });
        return (int)t.GetField("Count").GetValue(dag);
    }
}
