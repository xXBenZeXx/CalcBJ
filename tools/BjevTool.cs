using System;
using System.Globalization;
using System.Threading;
using CalcBJ.App;
using CalcBJ.Engine;

/// <summary>
/// Checks .bjev scenario files against the Windows app: "write" saves a sample with non-default settings, a
/// simulation and a compare entry; "read" opens a file exactly as the app does and prints what it got.
/// Compile with all src files and /main:BjevTool. Usage: BjevTool write|read &lt;file.bjev&gt;
/// </summary>
static class BjevTool
{
    static int Main(string[] args)
    {
        Thread.CurrentThread.CurrentCulture = CultureInfo.InvariantCulture;
        if (args.Length == 2 && args[0] == "write") { Write(args[1]); return 0; }
        if (args.Length == 2 && args[0] == "read") { Read(args[1]); return 0; }
        Console.WriteLine("usage: BjevTool write|read <file.bjev>");
        return 2;
    }

    static void Write(string path)
    {
        var s = new AppState();
        s.Rules.Decks = 2; s.Rules.H17 = false; s.Rules.Penetration = 0.7; s.Rules.Surrender = SurrenderRule.None;
        s.Rules.BlackjackPays = 1.2;
        s.Count = CountSystem.Presets()[11]; // Red Seven: separate red tags, running count
        s.Strategy.Mode = IndexMode.FullIndices;
        s.ResetIndexRange();
        var o = new IndexOverride();
        o.Kind = HandKind.Pair; o.Total = 10; o.Upcard = 6; o.Play = PlayKind.Split; o.Index = 4.5;
        s.Strategy.Overrides.Add(o);
        s.Spots = 2; s.AutoRoundsPerHour = false; s.RoundsPerHour = 120; s.Unit = 10; s.Bankroll = 12345.67;
        s.ResetRampRange();
        s.ApplyPreset(2);
        s.Ramp.Hands[s.Ramp.Rows - 1] = 2;
        s.TripHours = 25; s.TripBankroll = 4000;

        var pt = s.Table;
        var su = new SimSetup();
        su.Rules = s.Rules; su.Count = s.Count; su.Table = pt; su.Spots = s.Spots; su.Rounds = 500000; su.Threads = 4; su.Seed = 7;
        var sim = new Simulator(su);
        var done = new ManualResetEvent(false);
        sim.Finished += (a, b) => done.Set();
        sim.Start();
        done.WaitOne();
        var d = sim.Snapshot();
        d.Key = s.SimKey; d.RulesText = s.Rules.Describe(); d.CountText = s.CountText(); d.StrategyText = s.StrategyText();
        d.BasicEV = pt.BasicResult.PlayerEV; d.Metric = s.Count.Metric; d.Threads = 4;
        s.Sim = d;
        s.Compare.Add(s.Snapshot("Windows sample"));
        s.Save(path, true);
        Console.WriteLine("wrote " + path);
    }

    static void Read(string path)
    {
        var s = new AppState();
        s.Load(path);
        Console.WriteLine("Rules:    " + s.Rules.Describe());
        Console.WriteLine("Count:    " + s.CountText() + "   key " + s.Count.Key());
        Console.WriteLine("Strategy: " + s.StrategyText() + "   key " + s.Strategy.Key());
        Console.WriteLine("Table:    " + s.Spots + " spots, " + s.RoundsPerHour + " rounds/h (auto " + s.AutoRoundsPerHour + "), unit "
            + s.Unit + ", bankroll " + s.Bankroll);
        Console.WriteLine("Ramp:     " + s.RampText() + "   preset " + s.RampPreset);
        Console.WriteLine("Run:      " + s.SimRounds + " rounds per run, " + s.Threads + " threads; trip " + s.TripHours + " h with " + s.TripBankroll);
        if (s.Sim == null) Console.WriteLine("Sim:      none");
        else
        {
            var a = Analysis.Evaluate(s.Sim, s.Ramp, s.Unit, s.RoundsPerHour, s.Bankroll);
            Console.WriteLine("Sim:      " + s.Sim.Rounds + " rounds, " + s.Sim.Shoes + " shoes, created " + s.Sim.Created.ToString("o")
                + ", matches the settings: " + (s.Sim.Key == s.SimKey));
            Console.WriteLine("Result:   win " + a.WinPerHour.ToString("0.00") + "/h, SCORE " + a.Score.ToString("0.00") + ", ROR " + a.ROR.ToString("0.0000"));
        }
        foreach (var e in s.Compare)
            Console.WriteLine("Compare:  " + e.Name + " | " + e.Rules + " | " + e.Ramp + " | SCORE " + e.Score.ToString("0.00") + ", N0 " + e.N0
                + ", bankroll 5% " + e.Bankroll5 + ", stale " + e.Stale);
    }
}
