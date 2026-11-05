using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Windows.Forms;
using CalcBJ.App;
using CalcBJ.Engine;

// Drives the real UI in-process: compiled together with the app sources (/main:UiTest).
static class UiTest
{
    static MainForm form;
    static AppState state;
    static List<PageBase> pages;
    static string outDir;
    static int failures;

    static object Get(object o, string name)
    {
        var f = o.GetType().GetField(name, BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Public);
        if (f == null) throw new Exception("no field " + name + " on " + o.GetType().Name);
        return f.GetValue(o);
    }

    static object Call(object o, string name, params object[] args)
    {
        var m = o.GetType().GetMethod(name, BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Public);
        if (m == null) throw new Exception("no method " + name + " on " + o.GetType().Name);
        return m.Invoke(o, args);
    }

    static void Pump(int ms)
    {
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < ms) { Application.DoEvents(); Thread.Sleep(5); }
    }

    static void WaitSim()
    {
        var sw = Stopwatch.StartNew();
        int quiet = 0;
        while (sw.ElapsedMilliseconds < 120000)
        {
            Application.DoEvents();
            Thread.Sleep(10);
            var sim = form.CurrentSim;
            var deb = (System.Windows.Forms.Timer)Get(form, "debounce");
            bool busy = (sim != null && sim.IsRunning) || deb.Enabled || (bool)Get(form, "restartPending");
            quiet = busy ? 0 : quiet + 1;
            if (quiet > 30) return;
        }
        Fail("simulation did not finish");
    }

    static void Check(bool ok, string what)
    {
        Console.WriteLine((ok ? "  ok   " : "  FAIL ") + what);
        if (!ok) failures++;
    }

    static void Fail(string what) { Check(false, what); }

    static void Shot(string name)
    {
        form.Refresh();
        Application.DoEvents();
        using (var bmp = new Bitmap(form.Width, form.Height))
        {
            form.DrawToBitmap(bmp, new Rectangle(0, 0, form.Width, form.Height));
            bmp.Save(Path.Combine(outDir, name + ".png"), System.Drawing.Imaging.ImageFormat.Png);
        }
    }

    static void Show(int page)
    {
        Call(form, "SelectPage", pages[page]);
        Pump(150);
    }

    [STAThread]
    static void Main(string[] args)
    {
        outDir = args.Length > 0 ? args[0] : ".";
        Directory.CreateDirectory(outDir);
        Thread.CurrentThread.CurrentCulture = CultureInfo.InvariantCulture;
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.ThrowException);
        Theme.Init();
        if (args.Length > 1 && args[1] == "timing")
        {
            try { Timing(); }
            catch (Exception ex) { Console.WriteLine("EXCEPTION: " + ex); }
            if (form != null) form.Close();
            return;
        }
        try { Run(); }
        catch (Exception ex) { Console.WriteLine("EXCEPTION: " + ex); failures++; }
        Console.WriteLine(failures == 0 ? "ALL OK" : failures + " FAILURE(S)");
        if (form != null) form.Close();
    }

    static void Run()
    {
        form = new MainForm();
        state = (AppState)Get(form, "state");
        state.SimRounds = 50000000;
        form.Show();
        pages = (List<PageBase>)Get(form, "pages");

        Console.WriteLine("0. Manual-only simulation (default)");
        Pump(1200);
        Check(!state.AutoRun, "automatic re-run is off by default");
        Check(state.Sim == null && form.CurrentSim == null, "nothing simulated at startup");
        var g0 = pages[0];
        ((ComboBox)Get(g0, "decks")).SelectedIndex = 3;
        Pump(1000);
        Check(form.CurrentSim == null, "changing a setting does not start a simulation");
        form.StartSim();
        WaitSim();
        Check(state.Sim != null && state.Sim.Rounds == 50000000 && !state.SimStale, "Run simulates the current settings (4 decks)");
        ((ComboBox)Get(g0, "decks")).SelectedIndex = 5;
        Pump(1000);
        Check(!form.CurrentSim.IsRunning && state.SimStale, "after a change, results are marked out of date and nothing re-runs");
        form.StartSim();
        Pump(100);
        var started = form.CurrentSim;
        ((ComboBox)Get(g0, "soft17")).SelectedIndex = 0;
        // The Game page's analysis of the new rules can outlast this short run, so finishing it counts as keeping going.
        Check(form.CurrentSim == started && (started.IsRunning || (!started.Cancelled && started.Done == started.Target)),
            "a run you started keeps going when a setting changes");
        WaitSim();
        Check(state.SimStale, "its results are then shown as out of date");
        ((ComboBox)Get(g0, "soft17")).SelectedIndex = 1;
        Application.DoEvents();
        Check(!state.SimStale && state.Sim.Rounds == 50000000 && !form.CurrentSim.IsRunning, "switching back shows the stored results without re-running");
        state.AutoRun = true;   // the remaining steps exercise the optional auto-run mode

        Console.WriteLine("1. Startup");
        Check(state.Sim != null && state.Sim.Rounds >= 50000000, "initial simulation ran: " + (state.Sim == null ? 0 : state.Sim.Rounds));
        var a = state.Result;
        Check(a != null && a.Valid && a.WinPerHour > 0, "results valid, WR " + (a == null ? 0 : a.WinPerHour).ToString("0.00"));

        Console.WriteLine("2. Rules change via the Game page (S17, 8 decks)");
        var game = pages[0];
        ((ComboBox)Get(game, "soft17")).SelectedIndex = 0;
        ((ComboBox)Get(game, "decks")).SelectedIndex = 7;
        Check(!state.Rules.H17 && state.Rules.Decks == 8, "rules updated");
        Check(state.SimStale, "sim marked stale after change");
        WaitSim();
        Check(!state.SimStale && state.Sim.RulesText.Contains("8 decks"), "auto re-simulated: " + state.Sim.RulesText);
        Show(0); Shot("game_8d_s17");

        Console.WriteLine("2b. Result cache and accumulation");
        long r1 = state.Sim.Rounds;
        string k1 = state.SimKey;
        ((ComboBox)Get(game, "soft17")).SelectedIndex = 1;
        WaitSim();
        Check(state.Sim.Key == state.SimKey && state.SimKey != k1, "H17 simulated separately");
        var swc = Stopwatch.StartNew();
        ((ComboBox)Get(game, "soft17")).SelectedIndex = 0;
        Application.DoEvents();
        bool instant = state.Sim != null && state.Sim.Key == k1 && !state.SimStale && state.Sim.Rounds == r1;
        Check(instant, "back to S17: cached results shown instantly (" + swc.ElapsedMilliseconds + " ms)");
        Pump(600);
        Check(form.CurrentSim == null || !form.CurrentSim.IsRunning || state.Sim.Rounds == r1, "no re-simulation for a cached configuration");
        form.StartSim();
        WaitSim();
        Check(state.Sim.Rounds == r1 + state.SimRounds, "manual run added rounds: " + Theme.Count(r1) + " -> " + Theme.Count(state.Sim.Rounds));
        form.DiscardAndRun();
        WaitSim();
        Check(state.Sim.Rounds == state.SimRounds && state.CacheCount == 1, "discard & re-run started from scratch (" + Theme.Count(state.Sim.Rounds) + ")");

        Console.WriteLine("3. Switch to KO (running count)");
        var count = pages[1];
        Show(1);
        var preset = (ComboBox)Get(count, "preset");
        int ko = preset.Items.IndexOf("KO (Knock-Out)");
        preset.SelectedIndex = ko;
        Check(state.Count.Name == "KO (Knock-Out)" && state.Count.Metric == CountMetric.RunningCount, "KO selected, RC metric");
        Check(state.Ramp.Min < -20 && state.Ramp.Max > 0, "ramp range moved to RC values: " + state.Ramp.Min + ".." + state.Ramp.Max);
        WaitSim();
        a = state.Result;
        Check(a != null && a.Valid && a.WinPerHour > 0, "KO results valid, WR " + (a == null ? 0 : a.WinPerHour).ToString("0.00") + ", SCORE " + (a == null ? 0 : a.Score).ToString("0.0"));
        Pump(400);
        Shot("count_ko");
        Show(3); Shot("results_ko");

        Console.WriteLine("4. Red Seven (colour-dependent tags)");
        preset.SelectedIndex = preset.Items.IndexOf("Red Seven");
        Check(state.Count.RedTags != null, "red tags present");
        WaitSim();
        a = state.Result;
        Check(a != null && a.Valid, "Red Seven results valid, SCORE " + (a == null ? 0 : a.Score).ToString("0.0"));

        Console.WriteLine("5. Back to Hi-Lo, full indices, custom overrides");
        preset.SelectedIndex = preset.Items.IndexOf("Hi-Lo");
        ((ComboBox)Get(count, "indexMode")).SelectedIndex = 3;
        Check(state.Strategy.Mode == IndexMode.FullIndices, "full indices selected");
        Call(count, "LoadPublished");
        Check(state.Strategy.Overrides.Count == 22, "22 published overrides loaded: " + state.Strategy.Overrides.Count);
        var pt = state.Table;
        Check((pt.FlagsAt(0, PlayTable.HardCell(16, 10)) & Flag.Stand) != 0 && (pt.FlagsAt(-1, PlayTable.HardCell(16, 10)) & Flag.Stand) == 0,
            "override 16vT stand at >= 0 applied");
        Check(pt.InsureAt(3) && !pt.InsureAt(2), "insurance override at +3 applied");
        var view = (ComboBox)Get(count, "viewAt");
        view.SelectedIndex = view.Items.Count / 2 + 4;
        Pump(200);
        Shot("count_hilo_full_at_count");
        Show(1);
        ((Button)FindButton(count, "Clear")).PerformClick();
        Check(state.Strategy.Overrides.Count == 0, "overrides cleared");
        WaitSim();

        Console.WriteLine("6. Ramp editing and presets");
        var results = pages[3];
        Show(3);
        var ramp = (DataGridView)Get(results, "ramp");
        int row = ramp.Rows.Count - 1;
        ramp.Rows[row].Cells[3].Value = "20";
        Call(results, "SaveRampCell", row, 3);
        Check(Math.Abs(state.Ramp.Units[row] - 20) < 1e-9, "max bet edited to 20 units");
        ramp.Rows[row].Cells[4].Value = "2";
        Call(results, "SaveRampCell", row, 4);
        Check(state.Ramp.Hands[row] == 2, "two hands at top count");
        var pr = (ComboBox)Get(results, "preset");
        pr.SelectedIndex = 7;
        Check(state.Ramp.UnitsAt(state.Ramp.Min) == 0 && state.Result.PlayedFrac < 0.5, "back-count preset sits out low counts; played " + state.Result.PlayedFrac.ToString("0.000"));
        pr.SelectedIndex = 3;

        Console.WriteLine("7. Optimiser dialog");
        var t = new System.Windows.Forms.Timer();
        t.Interval = 300;
        t.Tick += (s, e) =>
        {
            foreach (Form f in Application.OpenForms)
                if (f is OptimizeDialog) { t.Stop(); ((Button)Get(f, "Ok")).PerformClick(); return; }
        };
        t.Start();
        double before = state.Result.Score;
        Call(results, "Optimise");
        double after = state.Result.Score;
        Check(after > before, "optimised SCORE " + after.ToString("0.00") + " > " + before.ToString("0.00"));
        Pump(200);
        Shot("results_optimised");

        Console.WriteLine("8. Compare, save and load");
        var cmp = pages[7];
        Show(7);
        ((Button)FindButton(cmp, "Add current scenario")).PerformClick();
        ((ComboBox)Get(game, "surrender")).SelectedIndex = 0;
        WaitSim();
        ((Button)FindButton(cmp, "Add current scenario")).PerformClick();
        Check(state.Compare.Count == 2, "two scenarios in compare");
        Show(7); Shot("compare");
        string file = Path.Combine(outDir, "test.bjev");
        state.Save(file, true);
        var savedKey = state.SimKey;
        var savedRamp = state.RampKey();
        long savedRounds = state.Sim.Rounds;
        ((ComboBox)Get(game, "decks")).SelectedIndex = 1;
        WaitSim();
        state.Load(file);
        Pump(300);
        Check(state.SimKey == savedKey, "loaded config key matches");
        Check(state.RampKey() == savedRamp, "loaded ramp matches");
        Check(state.Sim != null && state.Sim.Rounds == savedRounds && !state.SimStale, "loaded simulation data matches");
        Check(state.Compare.Count == 2, "compare list restored");

        Console.WriteLine("9. Every page renders");
        for (int i = 0; i < pages.Count; i++) { Show(i); Shot("final_page" + i); }
        string html = Report.Build(state);
        File.WriteAllText(Path.Combine(outDir, "report.html"), html);
        Check(html.Contains("Win rate") && html.Length > 3000, "HTML report built (" + html.Length + " chars)");

        Console.WriteLine("9b. Sensitivity sweeps");
        var sweep = pages[6];
        Show(6);
        ((ComboBox)Get(sweep, "param")).SelectedIndex = 4;
        ((Button)Get(sweep, "run")).PerformClick();
        var swGrid = (DataGridView)Get(sweep, "grid");
        var sw0 = Stopwatch.StartNew();
        while (((Thread)Get(sweep, "worker")).IsAlive && sw0.ElapsedMilliseconds < 60000) Pump(50);
        Pump(300);
        Check(swGrid.Rows.Count == 12, "max-bet sweep produced 12 points: " + swGrid.Rows.Count);
        if (swGrid.Rows.Count >= 2)
            Console.WriteLine("       1-2 SCORE " + swGrid.Rows[0].Cells[3].Value + "  ...  1-24 SCORE " + swGrid.Rows[swGrid.Rows.Count - 1].Cells[3].Value);
        Shot("sweep_maxbet");
        ((ComboBox)Get(sweep, "param")).SelectedIndex = 0;
        ((NumericUpDown)Get(sweep, "from")).Value = 65;
        ((NumericUpDown)Get(sweep, "to")).Value = 85;
        ((NumericUpDown)Get(sweep, "step")).Value = 10;
        ((ComboBox)Get(sweep, "rounds")).SelectedIndex = 0;
        ((Button)Get(sweep, "run")).PerformClick();
        sw0 = Stopwatch.StartNew();
        while (((Thread)Get(sweep, "worker")).IsAlive && sw0.ElapsedMilliseconds < 120000) Pump(50);
        Pump(300);
        Check(swGrid.Rows.Count == 3, "penetration sweep produced 3 points: " + swGrid.Rows.Count);
        for (int i = 0; i < swGrid.Rows.Count; i++)
            Console.WriteLine("       pen " + swGrid.Rows[i].Cells[0].Value + ": WR " + swGrid.Rows[i].Cells[1].Value + ", SCORE " + swGrid.Rows[i].Cells[3].Value);
        Shot("sweep_pen");

        Console.WriteLine("10. Odd rules: ENHC, 6:5, D10-11, no splits, 1 deck, 7 spots");
        ((ComboBox)Get(game, "hole")).SelectedIndex = 1;
        ((ComboBox)Get(game, "bj")).SelectedIndex = 2;
        ((ComboBox)Get(game, "dbl")).SelectedIndex = 2;
        ((ComboBox)Get(game, "splits")).SelectedIndex = 0;
        ((ComboBox)Get(game, "decks")).SelectedIndex = 0;
        ((NumericUpDown)Get(game, "spots")).Value = 7;
        WaitSim();
        a = state.Result;
        Check(a != null && a.Valid && state.Sim.Rounds >= 50000000, "1-deck 7-spot sim ran (" + state.Sim.Emergency + " mid-round reshuffles, "
            + ((double)state.Sim.Rounds / state.Sim.Shoes).ToString("0.00") + " rounds/shoe, flat EV "
            + Analysis.Evaluate(state.Sim, BetRamp.Create(0, 0, 1), 1, 100, 1000).EV.ToString("0.0000") + ")");
        Show(0); Shot("game_odd");
    }

    static void Timing()
    {
        var sw = Stopwatch.StartNew();
        var st0 = new AppState();
        Console.WriteLine("AppState                  {0,6} ms", sw.ElapsedMilliseconds);
        sw.Restart(); var c0 = ChartUtil.Create("x", "y", 100); Console.WriteLine("first Chart control       {0,6} ms", sw.ElapsedMilliseconds);
        sw.Restart(); var c1 = ChartUtil.Create("x", "y", 100); Console.WriteLine("second Chart control      {0,6} ms", sw.ElapsedMilliseconds);
        sw.Restart(); var dg = new DataGridView(); Theme.StyleGrid(dg); Console.WriteLine("first DataGridView        {0,6} ms", sw.ElapsedMilliseconds);
        sw.Restart(); CAEngine.HouseEdgeOffTop(st0.Rules); Console.WriteLine("first CA (builds DAGs)    {0,6} ms", sw.ElapsedMilliseconds);
        sw.Restart(); CAEngine.HouseEdgeOffTop(st0.Rules); Console.WriteLine("second CA                 {0,6} ms", sw.ElapsedMilliseconds);
        sw.Restart(); new GamePage(st0); Console.WriteLine("GamePage                  {0,6} ms", sw.ElapsedMilliseconds);
        sw.Restart(); new CountPage(st0); Console.WriteLine("CountPage                 {0,6} ms", sw.ElapsedMilliseconds);
        sw.Restart(); new ResultsPage(st0); Console.WriteLine("ResultsPage               {0,6} ms", sw.ElapsedMilliseconds);
        sw.Restart(); new RiskPage(st0); Console.WriteLine("RiskPage                  {0,6} ms", sw.ElapsedMilliseconds);
        sw.Restart(); new ChartsPage(st0); Console.WriteLine("ChartsPage                {0,6} ms", sw.ElapsedMilliseconds);
        sw.Restart(); new SweepPage(st0); Console.WriteLine("SweepPage                 {0,6} ms", sw.ElapsedMilliseconds);
        sw.Restart(); new ComparePage(st0); Console.WriteLine("ComparePage               {0,6} ms", sw.ElapsedMilliseconds);
        sw.Restart(); new GuidePage(st0); Console.WriteLine("GuidePage                 {0,6} ms", sw.ElapsedMilliseconds);
        sw.Restart();
        form = new MainForm();
        state = (AppState)Get(form, "state");
        state.AutoRun = true;
        Console.WriteLine("construct main form       {0,6} ms", sw.ElapsedMilliseconds);
        sw.Restart();
        form.Show();
        Application.DoEvents();
        Console.WriteLine("show + first paint        {0,6} ms", sw.ElapsedMilliseconds);
        pages = (List<PageBase>)Get(form, "pages");
        sw.Restart();
        WaitSim();
        Console.WriteLine("startup simulation (" + Theme.Count(state.Sim.Rounds) + ")  {0,6} ms", sw.ElapsedMilliseconds);
        for (int i = 0; i < pages.Count; i++)
        {
            sw.Restart();
            Call(form, "SelectPage", pages[i]);
            Application.DoEvents();
            form.Refresh();
            Console.WriteLine("switch to {0,-22}{1,6} ms", pages[i].Title, sw.ElapsedMilliseconds);
        }
        for (int i = 0; i < pages.Count; i++)
        {
            sw.Restart();
            Call(form, "SelectPage", pages[i]);
            long tSel = sw.ElapsedMilliseconds;
            sw.Restart();
            pages[i].OnActivated();
            long tAct = sw.ElapsedMilliseconds;
            sw.Restart();
            form.Refresh();
            long tPaint = sw.ElapsedMilliseconds;
            sw.Restart();
            form.Refresh();
            long tPaint2 = sw.ElapsedMilliseconds;
            Console.WriteLine("again: {0,-25} select {1,4}  activate-only {2,4}  paint {3,4}  repaint {4,4} ms", pages[i].Title, tSel, tAct, tPaint, tPaint2);
        }
        var game = pages[0];
        Call(form, "SelectPage", game);
        Pump(100);
        sw.Restart();
        ((ComboBox)Get(game, "soft17")).SelectedIndex = 0;
        Console.WriteLine("rules change (UI work)    {0,6} ms", sw.ElapsedMilliseconds);
        sw.Restart();
        var t0 = sw.ElapsedMilliseconds;
        long firstPartial = -1;
        long startRounds = state.Sim == null ? 0 : state.Sim.Rounds;
        while (sw.ElapsedMilliseconds < 60000)
        {
            Application.DoEvents();
            Thread.Sleep(5);
            if (firstPartial < 0 && state.Sim != null && !state.SimStale) firstPartial = sw.ElapsedMilliseconds;
            var sim = form.CurrentSim;
            var deb = (System.Windows.Forms.Timer)Get(form, "debounce");
            if (firstPartial >= 0 && sim != null && !sim.IsRunning && !deb.Enabled) break;
        }
        Console.WriteLine("change -> first results   {0,6} ms", firstPartial);
        Console.WriteLine("change -> final results   {0,6} ms ({1})", sw.ElapsedMilliseconds, Theme.Count(state.Sim.Rounds));
        var results = pages[3];
        Call(form, "SelectPage", results);
        Pump(100);
        var pr = (ComboBox)Get(results, "preset");
        sw.Restart();
        pr.SelectedIndex = 4;
        Application.DoEvents();
        Console.WriteLine("ramp preset change        {0,6} ms", sw.ElapsedMilliseconds);
    }

    static Control FindButton(Control root, string text)
    {
        foreach (Control c in root.Controls)
        {
            if (c is Button && c.Text == text) return c;
            var r = FindButton(c, text);
            if (r != null) return r;
        }
        return null;
    }
}
