using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Windows.Forms;
using CalcBJ.Engine;

namespace CalcBJ.App
{
    internal sealed class Chip : Control
    {
        string caption, value = "—";
        Color color = Theme.Text;

        public Chip(string c)
        {
            caption = c;
            SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            Size = new Size(Theme.S(112), Theme.S(44));
            Margin = new Padding(Theme.S(4), Theme.S(8), Theme.S(4), 0);
            BackColor = Theme.CardBg;
        }

        public void Set(string v, Color c) { value = v; color = c; Invalidate(); }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.Clear(Theme.CardBg);
            const TextFormatFlags tf = TextFormatFlags.Left | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix;
            TextRenderer.DrawText(g, caption.ToUpperInvariant(), Theme.SmallBold, new Rectangle(0, 0, Width, Theme.S(18)), Theme.Muted, tf);
            TextRenderer.DrawText(g, value, Theme.CardTitle, new Rectangle(0, Theme.S(17), Width, Theme.S(26)), color, tf);
        }
    }

    internal sealed class MainForm : Form
    {
        readonly AppState state = new AppState();
        readonly List<PageBase> pages = new List<PageBase>();
        readonly List<NavButton> navButtons = new List<NavButton>();
        readonly Panel content = new Panel();
        readonly Label pageTitle = new Label();
        readonly Label pageSub = new Label();
        readonly Chip chipWin = new Chip("Win rate / hr");
        readonly Chip chipSd = new Chip("SD / hr");
        readonly Chip chipScore = new Chip("SCORE");
        readonly Chip chipRor = new Chip("Risk of ruin");
        readonly Button runButton;
        readonly ProgressBar progress = new ProgressBar();
        readonly Label status = new Label();
        readonly Label simLine = new Label();
        readonly Timer poll = new Timer();
        readonly Timer debounce = new Timer();
        Simulator sim;
        bool restartPending;
        long lastRounds = -1;
        PageBase active;
        string simKey, simRules, simCount, simStrategy;
        double simBasic;
        CountMetric simMetric;

        public MainForm()
        {
            Text = "CalcBJ — Blackjack Advantage Calculator";
            Font = Theme.Body;
            BackColor = Theme.Bg;
            StartPosition = FormStartPosition.CenterScreen;
            var wa = Screen.PrimaryScreen.WorkingArea;
            Size = new Size(Math.Min(wa.Width - 40, Theme.S(1440)), Math.Min(wa.Height - 40, Theme.S(900)));
            MinimumSize = new Size(Theme.S(1100), Theme.S(680));
            KeyPreview = true;
            try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }

            runButton = Theme.Primary("▶  Run simulation");
            runButton.Click += (s, e) => { if (sim != null && sim.IsRunning) StopSim(); else StartSim(); };

            content.Dock = DockStyle.Fill;
            content.BackColor = Theme.Bg;
            Controls.Add(content);
            Controls.Add(BuildHeader());
            Controls.Add(BuildStatusBar());
            Controls.Add(BuildNav());

            AddPage(new GamePage(state), "♠", "Game rules");
            AddPage(new CountPage(state), "♣", "Count & strategy");
            AddPage(new SimPage(state, this), "▶", "Simulation");
            AddPage(new ResultsPage(state), "$", "Bet ramp & results");
            AddPage(new RiskPage(state), "⚖", "Bankroll & risk");
            AddPage(new ChartsPage(state), "▤", "Charts");
            AddPage(new SweepPage(state), "↗", "Sensitivity sweep");
            AddPage(new ComparePage(state), "⇄", "Compare games");
            AddPage(new GuidePage(state), "?", "Guide");
            for (int i = navButtons.Count - 1; i >= 0; i--) navPanel.Controls.Add(navButtons[i]);
            navPanel.Controls.Add(brand);

            poll.Interval = 150;
            poll.Tick += (s, e) => Poll();
            debounce.Interval = 300;
            debounce.Tick += (s, e) => { debounce.Stop(); if (state.AutoRun) StartSim(false); };

            state.ConfigChanged += (s, e) => OnConfigChanged();
            state.BettingChanged += (s, e) => UpdateHeader();
            state.SimChanged += (s, e) => UpdateHeader();
            state.Reloaded += (s, e) => { UpdateHeader(); if (state.AutoRun) StartSim(false); };

            SelectPage(pages[0]);
            UpdateHeader();
        }

        Panel navPanel;
        Panel brand;

        Control BuildNav()
        {
            navPanel = new Panel();
            navPanel.Dock = DockStyle.Left;
            navPanel.Width = Theme.S(216);
            navPanel.BackColor = Theme.NavBg;

            brand = new Panel();
            brand.Dock = DockStyle.Top;
            brand.Height = Theme.S(86);
            brand.BackColor = Theme.NavBg;
            brand.Paint += (s, e) =>
            {
                var g = e.Graphics;
                g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                int x = Theme.S(18), y = Theme.S(22), sz = Theme.S(34);
                using (var b = new SolidBrush(Theme.NavActive)) g.FillEllipse(b, x, y, sz, sz);
                TextRenderer.DrawText(g, "21", Theme.CardTitle, new Rectangle(x, y, sz, sz), Color.White, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
                TextRenderer.DrawText(g, "CalcBJ", Theme.Brand, new Point(x + sz + Theme.S(8), y - Theme.S(4)), Color.White);
                TextRenderer.DrawText(g, "Blackjack EV lab", Theme.Small, new Point(x + sz + Theme.S(10), y + Theme.S(22)), Theme.NavText);
            };

            var files = new FlowLayoutPanel();
            files.Dock = DockStyle.Bottom;
            files.FlowDirection = FlowDirection.TopDown;
            files.AutoSize = true;
            files.Padding = new Padding(Theme.S(14), Theme.S(8), Theme.S(8), Theme.S(14));
            files.BackColor = Theme.NavBg;
            files.Controls.Add(NavLink("Open scenario…   Ctrl+O", (s, e) => OpenFile()));
            files.Controls.Add(NavLink("Save scenario…   Ctrl+S", (s, e) => SaveFile(false)));
            files.Controls.Add(NavLink("Export report (HTML)…", (s, e) => ExportReport()));
            navPanel.Controls.Add(files);
            return navPanel;
        }

        static LinkLabel NavLink(string text, EventHandler onClick)
        {
            var l = new LinkLabel();
            l.Text = text;
            l.AutoSize = true;
            l.LinkColor = Theme.NavText;
            l.ActiveLinkColor = Color.White;
            l.VisitedLinkColor = Theme.NavText;
            l.LinkBehavior = LinkBehavior.HoverUnderline;
            l.Font = Theme.Body;
            l.Margin = new Padding(0, Theme.S(4), 0, Theme.S(4));
            l.Click += onClick;
            return l;
        }

        Control BuildHeader()
        {
            var header = new TableLayoutPanel();
            header.Dock = DockStyle.Top;
            header.Height = Theme.S(80);
            header.BackColor = Theme.CardBg;
            header.ColumnCount = 3;
            header.RowCount = 1;
            header.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            header.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            header.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            header.Padding = new Padding(Theme.S(20), Theme.S(8), Theme.S(16), Theme.S(6));
            header.Paint += (s, e) => { using (var p = new Pen(Theme.Border)) e.Graphics.DrawLine(p, 0, header.Height - 1, header.Width, header.Height - 1); };

            var titles = new TableLayoutPanel();
            titles.ColumnCount = 1; titles.RowCount = 2; titles.Dock = DockStyle.Fill; titles.Margin = Padding.Empty;
            pageTitle.Font = Theme.PageTitle; pageTitle.ForeColor = Theme.Text; pageTitle.AutoSize = true; pageTitle.Margin = new Padding(0, Theme.S(4), 0, 0);
            pageSub.Font = Theme.Body; pageSub.ForeColor = Theme.Muted; pageSub.AutoSize = true; pageSub.Margin = new Padding(Theme.S(2), Theme.S(2), 0, 0);
            pageTitle.UseMnemonic = false; pageSub.UseMnemonic = false;
            titles.Controls.Add(pageTitle, 0, 0);
            titles.Controls.Add(pageSub, 0, 1);
            header.Controls.Add(titles, 0, 0);

            var chips = new FlowLayoutPanel();
            chips.AutoSize = true; chips.WrapContents = false; chips.Margin = new Padding(0, 0, Theme.S(12), 0);
            chips.Controls.Add(chipWin); chips.Controls.Add(chipSd); chips.Controls.Add(chipScore); chips.Controls.Add(chipRor);
            header.Controls.Add(chips, 1, 0);

            var run = new TableLayoutPanel();
            run.ColumnCount = 1; run.RowCount = 2; run.AutoSize = true; run.Margin = new Padding(0, Theme.S(4), 0, 0);
            runButton.MinimumSize = new Size(Theme.S(176), Theme.S(34));
            runButton.Margin = new Padding(0);
            progress.Width = Theme.S(176); progress.Height = Theme.S(6); progress.Maximum = 1000; progress.Margin = new Padding(0, Theme.S(6), 0, 0);
            progress.Style = ProgressBarStyle.Continuous;
            run.Controls.Add(runButton, 0, 0);
            run.Controls.Add(progress, 0, 1);
            header.Controls.Add(run, 2, 0);
            return header;
        }

        Control BuildStatusBar()
        {
            var bar = new Panel();
            bar.Dock = DockStyle.Bottom;
            bar.Height = Theme.S(28);
            bar.BackColor = Theme.CardBg;
            bar.Paint += (s, e) => { using (var p = new Pen(Theme.Border)) e.Graphics.DrawLine(p, 0, 0, bar.Width, 0); };
            status.Dock = DockStyle.Fill;
            status.TextAlign = ContentAlignment.MiddleLeft;
            status.Font = Theme.Small;
            status.ForeColor = Theme.Muted;
            status.Padding = new Padding(Theme.S(12), 0, 0, 0);
            simLine.Dock = DockStyle.Right;
            simLine.AutoSize = false;
            simLine.Width = Theme.S(520);
            simLine.TextAlign = ContentAlignment.MiddleRight;
            simLine.Font = Theme.Small;
            simLine.ForeColor = Theme.Muted;
            simLine.Padding = new Padding(0, 0, Theme.S(12), 0);
            bar.Controls.Add(status);
            bar.Controls.Add(simLine);
            return bar;
        }

        void AddPage(PageBase page, string glyph, string label)
        {
            pages.Add(page);
            page.Visible = false;
            content.Controls.Add(page);
            UiLayout.Finish(page);
            var b = new NavButton(glyph, label);
            b.Click += (s, e) => SelectPage(page);
            navButtons.Add(b);
        }

        public void ShowPage(Type t)
        {
            foreach (var p in pages) if (p.GetType() == t) { SelectPage(p); return; }
        }

        void SelectPage(PageBase page)
        {
            SuspendLayout();
            foreach (var p in pages) p.Visible = p == page;
            for (int i = 0; i < pages.Count; i++) navButtons[i].Active = pages[i] == page;
            active = page;
            pageTitle.Text = page.Title;
            pageSub.Text = page.Subtitle;
            ResumeLayout();
            page.OnActivated();
        }

        /// <summary>Test hook: when set, the app simulates, renders every page to PNG files here and exits.</summary>
        public string ScreenshotDir;
        public string StartupFile;

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            if (!string.IsNullOrEmpty(StartupFile))
            {
                try { state.Load(StartupFile); }
                catch (Exception ex) { MessageBox.Show(this, "Could not open " + StartupFile + ":\n" + ex.Message, "CalcBJ"); }
            }
            if (state.AutoRun || ScreenshotDir != null) StartSim(false);
        }

        void TakeScreenshots()
        {
            Directory.CreateDirectory(ScreenshotDir);
            for (int i = 0; i < pages.Count; i++)
            {
                SelectPage(pages[i]);
                Refresh();
                Application.DoEvents();
                using (var bmp = new Bitmap(Width, Height))
                {
                    DrawToBitmap(bmp, new Rectangle(0, 0, Width, Height));
                    bmp.Save(Path.Combine(ScreenshotDir, "page" + i + ".png"), System.Drawing.Imaging.ImageFormat.Png);
                }
            }
            Close();
        }

        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            if (keyData == Keys.F5) { StartSim(); return true; }
            if (keyData == (Keys.Control | Keys.S)) { SaveFile(false); return true; }
            if (keyData == (Keys.Control | Keys.O)) { OpenFile(); return true; }
            if (keyData == Keys.Escape && sim != null && sim.IsRunning) { StopSim(); return true; }
            return base.ProcessCmdKey(ref msg, keyData);
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (sim != null) sim.Stop();
            base.OnFormClosing(e);
        }

        // ---------------- simulation control ----------------
        //
        // By default the simulator only runs when the user presses Run (F5); changing settings never starts or stops a
        // run. Results are kept per configuration, so going back to settings that were already simulated shows those
        // results at once, and a manual run adds rounds to them. The optional auto-run mode re-simulates (tops up to
        // the target) a moment after each change.

        SimData baseData;
        bool restartManual;

        void OnConfigChanged()
        {
            string key = state.SimKey;
            var cached = state.Cached(key);
            bool runningThis = sim != null && sim.IsRunning && simKey == key;
            if (cached != null && !runningThis && state.Sim != cached) { state.Sim = cached; state.NotifySim(); }
            UpdateHeader();
            if (!state.AutoRun) return;
            if (sim != null && sim.IsRunning && simKey != key) { restartPending = false; sim.Stop(); }
            if (!runningThis && (cached == null || cached.Rounds < state.SimRounds))
            {
                debounce.Stop();
                debounce.Start();
            }
        }

        /// <summary>Manual run: adds the configured number of rounds to any results already held for these settings.</summary>
        public void StartSim() { StartSim(true); }

        /// <summary>Throws away every stored result and simulates the current settings from scratch.</summary>
        public void DiscardAndRun()
        {
            if (sim != null && sim.IsRunning) { restartPending = false; sim.Stop(); }
            state.ClearCache();
            state.Sim = null;
            baseData = null;
            state.NotifySim();
            BeginInvoke(new Action(() => StartSim(true)));
        }

        void StartSim(bool manual)
        {
            debounce.Stop();
            if (sim != null && sim.IsRunning) { restartPending = true; restartManual = manual; sim.Stop(); return; }
            string key = state.SimKey;
            var existing = state.Cached(key);
            long rounds = state.SimRounds;
            if (!manual && existing != null)
            {
                rounds = state.SimRounds - existing.Rounds;
                if (rounds < Simulator.Chunk)
                {
                    if (state.Sim != existing) { state.Sim = existing; state.NotifySim(); }
                    UpdateHeader();
                    if (ScreenshotDir != null) BeginInvoke(new Action(TakeScreenshots));
                    return;
                }
            }
            PlayTable pt;
            try
            {
                Cursor = Cursors.WaitCursor;
                pt = state.Table;
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, "Could not build the playing strategy:\n" + ex.Message, "CalcBJ", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }
            finally { Cursor = Cursors.Default; }
            var su = new SimSetup();
            su.Rules = state.Rules.Clone();
            su.Count = state.Count.Clone();
            su.Table = pt;
            su.Spots = state.Spots;
            su.Rounds = Math.Max(Simulator.Chunk, rounds);
            su.Threads = Math.Max(1, state.Threads);
            su.Seed = (ulong)DateTime.UtcNow.Ticks ^ 0x5DEECE66DUL;
            sim = new Simulator(su);
            baseData = existing;
            simKey = key;
            simRules = state.Rules.Describe();
            simCount = state.CountText();
            simStrategy = state.StrategyText();
            simBasic = pt.BasicResult.PlayerEV;
            simMetric = state.Count.Metric;
            lastRounds = -1;
            sim.Start();
            state.SimRunning = true;
            poll.Start();
            UpdateHeader();
        }

        public void StopSim()
        {
            restartPending = false;
            if (sim != null) sim.Stop();
        }

        public Simulator CurrentSim { get { return sim; } }

        void Poll()
        {
            if (sim == null) return;
            bool running = sim.IsRunning;
            long done = sim.Done;
            if (done != lastRounds || !running)
            {
                lastRounds = done;
                if (done > 0)
                {
                    var snap = sim.Snapshot();
                    snap.Key = simKey;
                    snap.RulesText = simRules;
                    snap.CountText = simCount;
                    snap.StrategyText = simStrategy;
                    snap.BasicEV = simBasic;
                    snap.Metric = simMetric;
                    snap.Threads = sim.Setup.Threads;
                    var merged = SimData.Merge(baseData, snap);
                    if (!running) state.Remember(merged);
                    // Results for settings that are no longer selected only stay on screen if nothing better is held.
                    if (simKey == state.SimKey || state.Cached(state.SimKey) == null) state.Sim = merged;
                }
                state.SimProgress = (double)done / sim.Target;
                state.NotifySim();
            }
            progress.Value = (int)Math.Max(0, Math.Min(1000, 1000.0 * done / sim.Target));
            if (!running)
            {
                poll.Stop();
                state.SimRunning = false;
                if (sim.Error != null)
                    MessageBox.Show(this, "Simulation failed:\n" + sim.Error.Message, "CalcBJ", MessageBoxButtons.OK, MessageBoxIcon.Error);
                state.NotifySim();
                UpdateHeader();
                if (restartPending) { restartPending = false; StartSim(restartManual); }
                else if (ScreenshotDir != null) BeginInvoke(new Action(TakeScreenshots));
            }
            else UpdateHeader();
        }

        void UpdateHeader()
        {
            var a = state.Result;
            if (a != null && a.Valid)
            {
                chipWin.Set(Theme.Money(a.WinPerHour), a.WinPerHour >= 0 ? Theme.Good : Theme.Bad);
                chipSd.Set(Theme.Money0(a.SdPerHour), Theme.Text);
                chipScore.Set(Theme.F(a.Score, "0.0"), Theme.Text);
                chipRor.Set(Theme.Ror(a.ROR), a.ROR > 0.2 ? Theme.Bad : Theme.Text);
            }
            else
            {
                chipWin.Set("—", Theme.Muted); chipSd.Set("—", Theme.Muted); chipScore.Set("—", Theme.Muted); chipRor.Set("—", Theme.Muted);
            }
            var d = state.Sim;
            bool busy = sim != null && sim.IsRunning;
            if (busy)
            {
                runButton.Text = "■  Stop";
                runButton.BackColor = Theme.Bad;
                runButton.FlatAppearance.MouseOverBackColor = Color.FromArgb(160, 40, 30);
            }
            else
            {
                runButton.Text = d != null && !state.SimStale ? "▶  Add " + Theme.Count(state.SimRounds) + " rounds" : "▶  Run simulation";
                runButton.BackColor = Theme.Accent;
                runButton.FlatAppearance.MouseOverBackColor = Theme.AccentDark;
            }
            if (busy)
            {
                double secs = Math.Max(0.05, sim.ElapsedSeconds);
                double rate = sim.Done / secs;
                double eta = rate > 0 ? (sim.Target - sim.Done) / rate : 0;
                simLine.Text = string.Format(CultureInfo.InvariantCulture, "Simulating… {0} / {1} rounds · {2:0.0}M rounds/s · {3:0}s left",
                    Theme.Count(sim.Done), Theme.Count(sim.Target), rate / 1e6, eta);
            }
            else if (d != null)
                simLine.Text = string.Format(CultureInfo.InvariantCulture, "{0} rounds simulated in {1:0.0}s ({2:0.0}M rounds/s)",
                    Theme.Count(d.Rounds), d.Seconds, d.Seconds > 0 ? d.Rounds / d.Seconds / 1e6 : 0);
            else simLine.Text = "No simulation yet";

            string stale = state.SimStale ? "  ·  ⚠ settings changed since this simulation — re-run (F5)" : "";
            status.Text = state.Rules.Describe() + "  ·  " + state.CountText() + "  ·  " + state.StrategyText() + "  ·  " + state.Spots + (state.Spots == 1 ? " spot" : " spots") + stale;
            status.ForeColor = state.SimStale ? Theme.Warn : Theme.Muted;
        }

        // ---------------- files ----------------

        void SaveFile(bool saveAs)
        {
            string path = state.FilePath;
            if (saveAs || string.IsNullOrEmpty(path))
            {
                using (var dlg = new SaveFileDialog())
                {
                    dlg.Filter = "CalcBJ scenario (*.bjev)|*.bjev|All files (*.*)|*.*";
                    dlg.FileName = "scenario.bjev";
                    if (dlg.ShowDialog(this) != DialogResult.OK) return;
                    path = dlg.FileName;
                }
            }
            try
            {
                state.Save(path, true);
                status.Text = "Saved " + path;
            }
            catch (Exception ex) { MessageBox.Show(this, "Could not save:\n" + ex.Message, "CalcBJ", MessageBoxButtons.OK, MessageBoxIcon.Error); }
        }

        void OpenFile()
        {
            using (var dlg = new OpenFileDialog())
            {
                dlg.Filter = "CalcBJ scenario (*.bjev)|*.bjev|All files (*.*)|*.*";
                if (dlg.ShowDialog(this) != DialogResult.OK) return;
                try
                {
                    if (sim != null && sim.IsRunning) { restartPending = false; sim.Stop(); }
                    state.Load(dlg.FileName);
                }
                catch (Exception ex) { MessageBox.Show(this, "Could not open the file:\n" + ex.Message, "CalcBJ", MessageBoxButtons.OK, MessageBoxIcon.Error); }
            }
        }

        void ExportReport()
        {
            if (state.Sim == null) { MessageBox.Show(this, "Run a simulation first.", "CalcBJ"); return; }
            using (var dlg = new SaveFileDialog())
            {
                dlg.Filter = "HTML report (*.html)|*.html";
                dlg.FileName = "calcbj-report.html";
                if (dlg.ShowDialog(this) != DialogResult.OK) return;
                try
                {
                    File.WriteAllText(dlg.FileName, Report.Build(state), new System.Text.UTF8Encoding(false));
                    try { System.Diagnostics.Process.Start(dlg.FileName); } catch { }
                }
                catch (Exception ex) { MessageBox.Show(this, "Could not write the report:\n" + ex.Message, "CalcBJ", MessageBoxButtons.OK, MessageBoxIcon.Error); }
            }
        }
    }
}
