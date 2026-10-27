<script lang="ts">
  import { onMount, untrack } from "svelte";
  import Banner from "./lib/components/Banner.svelte";
  import { engine } from "./lib/engine/engine";
  import { openScenario, openScenarioDialog, saveScenario } from "./lib/files";
  import { count, fixed, money, money0, pct } from "./lib/format";
  import { simulation } from "./lib/simulation.svelte";
  import { app, type PageId } from "./lib/state.svelte";
  import { sweep } from "./lib/sweep.svelte";
  import { countText, strategyText } from "./lib/text";
  import ChartsPage from "./pages/ChartsPage.svelte";
  import ComparePage from "./pages/ComparePage.svelte";
  import CountPage from "./pages/CountPage.svelte";
  import GamePage from "./pages/GamePage.svelte";
  import GuidePage from "./pages/GuidePage.svelte";
  import ResultsPage from "./pages/ResultsPage.svelte";
  import RiskPage from "./pages/RiskPage.svelte";
  import SimPage from "./pages/SimPage.svelte";
  import SweepPage from "./pages/SweepPage.svelte";

  interface Page {
    id: PageId;
    glyph: string;
    label: string;
    short: string;
    subtitle: string;
  }

  const pages: Page[] = [
    { id: "game", glyph: "♠", label: "Game rules", short: "Rules", subtitle: "Rules, shoe and table. The basic-strategy house edge is recalculated instantly." },
    { id: "count", glyph: "♣", label: "Count & strategy", short: "Count", subtitle: "Counting system, true-count conversion and the index plays used in the simulation." },
    { id: "sim", glyph: "▶", label: "Simulation", short: "Simulate", subtitle: "Simulate the game once; every bet ramp is then evaluated instantly from the per-count results." },
    { id: "results", glyph: "$", label: "Bet ramp & results", short: "Results", subtitle: "Set your bets by count; win rate, risk and SCORE update instantly from the simulation." },
    { id: "risk", glyph: "⚖", label: "Bankroll & risk", short: "Risk", subtitle: "Risk of ruin, bankroll requirements and what a trip can look like, for the current bet ramp." },
    { id: "charts", glyph: "▤", label: "Charts", short: "Charts", subtitle: "How often each count comes up, what it is worth, and how your bets line up with it." },
    { id: "sweep", glyph: "↗", label: "Sensitivity sweep", short: "Sweep", subtitle: "How win rate, SCORE and risk change as one parameter varies — everything else stays as set." },
    { id: "compare", glyph: "⇄", label: "Compare games", short: "Compare", subtitle: "Snapshot the current game, ramp and results, then line several up side by side." },
    { id: "guide", glyph: "?", label: "Guide", short: "Guide", subtitle: "How CalcBJ works and what the numbers mean." },
  ];
  /** On phones the first four are tabs and the rest sit under More. */
  const TABS = 4;

  let current = $derived(pages.find((p) => p.id === app.page) ?? pages[0]);
  let moreOpen = $state(false);
  let notice = $state<{ text: string; tone: "info" | "warn" } | null>(null);
  let fileInput: HTMLInputElement;

  // Settings are kept in the browser.
  $effect(() => app.save());

  // Going back to settings that were already simulated shows those results at once.
  $effect(() => {
    const key = app.simKey;
    untrack(() => app.showCached(key));
  });

  // ---------------- installing ----------------

  interface InstallPrompt extends Event {
    prompt(): Promise<void>;
  }
  let install = $state<InstallPrompt | null>(null);

  onMount(() => {
    const offer = (e: Event) => {
      e.preventDefault();
      install = e as InstallPrompt;
    };
    const installed = () => (install = null);
    window.addEventListener("beforeinstallprompt", offer);
    window.addEventListener("appinstalled", installed);
    return () => {
      window.removeEventListener("beforeinstallprompt", offer);
      window.removeEventListener("appinstalled", installed);
    };
  });

  async function installApp() {
    moreOpen = false;
    await install?.prompt();
    install = null;
  }

  // ---------------- files ----------------

  async function save() {
    moreOpen = false;
    try {
      const name = await saveScenario();
      if (name) notice = { text: __DESKTOP__ ? `Saved ${name}.` : `Saved ${name} to your downloads.`, tone: "info" };
    } catch (e) {
      notice = { text: "Could not save: " + (e instanceof Error ? e.message : String(e)), tone: "warn" };
    }
  }

  async function open() {
    moreOpen = false;
    if (!__DESKTOP__) {
      fileInput.click();
      return;
    }
    try {
      const name = await openScenarioDialog();
      if (name) notice = { text: `Opened ${name}.`, tone: "info" };
    } catch (err) {
      notice = { text: "Could not open the file: " + (err instanceof Error ? err.message : String(err)), tone: "warn" };
    }
  }

  async function opened(e: Event) {
    const input = e.currentTarget as HTMLInputElement;
    const file = input.files?.[0];
    input.value = "";
    if (!file) return;
    try {
      await openScenario(file);
      notice = { text: `Opened ${file.name}.`, tone: "info" };
    } catch (err) {
      notice = { text: "Could not open the file: " + (err instanceof Error ? err.message : String(err)), tone: "warn" };
    }
  }

  function go(id: PageId) {
    app.page = id;
    moreOpen = false;
    scrollTo(0, 0);
  }

  // ---------------- header and status ----------------

  let result = $derived(app.evaluation?.result.valid ? app.evaluation.result : null);
  let rate = $derived(simulation.seconds > 0 ? simulation.done / simulation.seconds : 0);
  let progress = $derived(simulation.target > 0 ? Math.min(100, (100 * simulation.done) / simulation.target) : 0);

  let runLabel = $derived(
    sweep.running
      ? "■  Stop sweep"
      : simulation.running
        ? "■  Stop"
        : app.sim && !app.simStale
          ? `▶  Add ${count(app.simRounds)} rounds`
          : "▶  Run simulation",
  );

  let simLine = $derived.by(() => {
    if (simulation.running) {
      const eta = rate > 0 ? (simulation.target - simulation.done) / rate : 0;
      const what = sweep.running ? "Sweep point: " : "Simulating… ";
      return `${what}${count(simulation.done)} / ${count(simulation.target)} rounds · ${(rate / 1e6).toFixed(1)}M rounds/s · ${eta.toFixed(0)}s left`;
    }
    const d = app.sim;
    if (!d) return "No simulation yet";
    const speed = d.seconds > 0 ? d.rounds / d.seconds / 1e6 : 0;
    return `${count(d.rounds)} rounds simulated in ${d.seconds.toFixed(1)}s (${speed.toFixed(1)}M rounds/s)`;
  });

  let status = $derived.by(() => {
    let rules = "";
    try {
      rules = engine.rulesInfo(app.rules, app.spots).describe;
    } catch {
      rules = "invalid rules";
    }
    const spots = `${app.spots} ${app.spots === 1 ? "spot" : "spots"}`;
    return `${rules} · ${countText(app.count)} · ${strategyText(app.strategy)} · ${spots}`;
  });

  function onkeydown(e: KeyboardEvent) {
    const mod = e.ctrlKey || e.metaKey;
    if (e.key === "F5") {
      e.preventDefault();
      app.run();
    } else if (e.key === "Escape" && simulation.running) {
      app.stop();
    } else if (mod && e.key.toLowerCase() === "s") {
      e.preventDefault();
      save();
    } else if (mod && e.key.toLowerCase() === "o") {
      e.preventDefault();
      open();
    }
  }
</script>

<svelte:window {onkeydown} />

<input bind:this={fileInput} type="file" accept=".bjev,application/xml,text/xml" hidden onchange={opened} />

<div class="shell">
  <aside class="sidebar">
    <div class="brand">
      <span class="logo">21</span>
      <div>
        <div class="name">CalcBJ</div>
        <div class="tagline">Blackjack EV lab</div>
      </div>
    </div>
    <nav>
      {#each pages as p (p.id)}
        <button class="nav" class:active={p.id === app.page} onclick={() => go(p.id)}>
          <span class="glyph">{p.glyph}</span>{p.label}
        </button>
      {/each}
    </nav>
    <div class="files">
      <button class="link" onclick={open}>Open scenario…<span>Ctrl+O</span></button>
      <button class="link" onclick={save}>Save scenario…<span>Ctrl+S</span></button>
      {#if install}<button class="link" onclick={installApp}>Install app…</button>{/if}
    </div>
  </aside>

  <div class="main">
    <header class="topbar">
      <div class="titles">
        <h1><span class="mobile-logo">21</span>{current.label}</h1>
        <p class="subtitle">{current.subtitle}</p>
      </div>
      <div class="chips">
        <div class="chip">
          <span>Win rate / hr</span>
          <b class:good={!!result && result.winPerHour >= 0} class:bad={!!result && result.winPerHour < 0}>
            {result ? money(result.winPerHour) : "—"}
          </b>
        </div>
        <div class="chip"><span>SD / hr</span><b>{result ? money0(result.sdPerHour) : "—"}</b></div>
        <div class="chip"><span>SCORE</span><b>{result ? fixed(result.score, 1) : "—"}</b></div>
        <div class="chip">
          <span>Risk of ruin</span>
          <b class:bad={!!result && result.ror > 0.2}>{result ? pct(result.ror, result.ror < 0.001 ? 3 : 1) : "—"}</b>
        </div>
      </div>
      <div class="run">
        <button
          class="btn {simulation.running ? 'btn-danger' : 'btn-primary'}"
          onclick={() => (sweep.running ? sweep.stop() : app.toggleRun())}
        >
          {runLabel}
        </button>
        <div class="progress" aria-hidden="true"><div style:width="{simulation.running ? progress : 0}%"></div></div>
      </div>
    </header>

    <main class="page">
      {#if notice}
        <div class="notice">
          <Banner text={notice.text} tone={notice.tone} />
          <button class="dismiss" aria-label="Dismiss" onclick={() => (notice = null)}>✕</button>
        </div>
      {/if}
      {#if simulation.error}
        <p class="error">Simulation stopped: {simulation.error}</p>
      {/if}
      {#if app.page === "game"}
        <GamePage />
      {:else if app.page === "count"}
        <CountPage />
      {:else if app.page === "sim"}
        <SimPage />
      {:else if app.page === "results"}
        <ResultsPage />
      {:else if app.page === "risk"}
        <RiskPage />
      {:else if app.page === "charts"}
        <ChartsPage />
      {:else if app.page === "sweep"}
        <SweepPage />
      {:else if app.page === "compare"}
        <ComparePage />
      {:else}
        <GuidePage />
      {/if}
    </main>

    <footer class="statusbar">
      <span class:warn={app.simStale}>
        {status}{app.simStale ? "  ·  ⚠ settings changed since this simulation — re-run (F5)" : ""}
      </span>
      <span class="sim-line">{simLine}</span>
    </footer>
  </div>

  {#if moreOpen}
    <button class="scrim" aria-label="Close menu" onclick={() => (moreOpen = false)}></button>
    <div class="sheet" role="menu">
      {#each pages.slice(TABS) as p (p.id)}
        <button role="menuitem" class:active={p.id === app.page} onclick={() => go(p.id)}>
          <span class="glyph">{p.glyph}</span>{p.label}
        </button>
      {/each}
      <hr />
      <button role="menuitem" onclick={open}><span class="glyph">↥</span>Open scenario…</button>
      <button role="menuitem" onclick={save}><span class="glyph">↧</span>Save scenario…</button>
      {#if install}<button role="menuitem" onclick={installApp}><span class="glyph">⊕</span>Install app…</button>{/if}
    </div>
  {/if}

  <nav class="tabbar">
    {#each pages.slice(0, TABS) as p (p.id)}
      <button class:active={p.id === app.page && !moreOpen} onclick={() => go(p.id)}>
        <span class="glyph">{p.glyph}</span><span>{p.short}</span>
      </button>
    {/each}
    <button
      class:active={moreOpen || pages.findIndex((p) => p.id === app.page) >= TABS}
      aria-expanded={moreOpen}
      onclick={() => (moreOpen = !moreOpen)}
    >
      <span class="glyph">⋯</span><span>More</span>
    </button>
  </nav>
</div>

<style>
  .shell {
    display: grid;
    grid-template-columns: 216px minmax(0, 1fr);
    min-height: 100vh;
  }

  .sidebar {
    background: var(--nav-bg);
    color: var(--nav-text);
    position: sticky;
    top: 0;
    height: 100vh;
    display: flex;
    flex-direction: column;
    overflow-y: auto;
  }

  .brand {
    display: flex;
    align-items: center;
    gap: 10px;
    padding: 22px 18px 20px;
  }

  .logo,
  .mobile-logo {
    display: inline-grid;
    place-items: center;
    width: 34px;
    height: 34px;
    border-radius: 50%;
    background: var(--nav-active);
    color: #fff;
    font-weight: 600;
    flex: none;
  }

  .name {
    color: #fff;
    font-size: 20px;
    font-weight: 600;
    line-height: 1.1;
  }

  .tagline {
    font-size: 12px;
  }

  .nav {
    display: flex;
    align-items: center;
    gap: 12px;
    width: 100%;
    border: 0;
    background: none;
    color: var(--nav-text);
    padding: 9px 18px;
    font-size: 14.5px;
    text-align: left;
    cursor: pointer;
    border-left: 3px solid transparent;
  }

  .nav:hover {
    background: var(--nav-hover);
  }

  .nav.active {
    background: var(--nav-hover);
    color: #fff;
    border-left-color: var(--nav-active);
  }

  .glyph {
    width: 18px;
    text-align: center;
    font-size: 15px;
  }

  .files {
    margin-top: auto;
    padding: 12px 18px 16px;
    display: flex;
    flex-direction: column;
    gap: 2px;
  }

  .link {
    display: flex;
    justify-content: space-between;
    gap: 8px;
    border: 0;
    background: none;
    color: var(--nav-text);
    padding: 5px 0;
    text-align: left;
    cursor: pointer;
  }

  .link span {
    opacity: 0.6;
    font-size: 12px;
  }

  .link:hover {
    color: #fff;
  }

  .main {
    display: flex;
    flex-direction: column;
    min-width: 0;
  }

  .topbar {
    position: sticky;
    top: 0;
    z-index: 5;
    display: flex;
    align-items: center;
    gap: 16px;
    padding: 10px 20px;
    background: var(--card);
    border-bottom: 1px solid var(--border);
  }

  .titles {
    flex: 1;
    min-width: 0;
  }

  h1 {
    font-size: 20px;
    font-weight: 600;
    display: flex;
    align-items: center;
    gap: 10px;
  }

  .mobile-logo {
    display: none;
    width: 28px;
    height: 28px;
    font-size: 12.5px;
  }

  .subtitle {
    color: var(--muted);
    font-size: 13px;
  }

  .chips {
    display: flex;
    gap: 14px;
  }

  .chip {
    display: flex;
    flex-direction: column;
    min-width: 96px;
  }

  .chip span {
    font-size: 11px;
    font-weight: 600;
    color: var(--muted);
    text-transform: uppercase;
    letter-spacing: 0.02em;
  }

  .chip b {
    font-size: 17px;
    font-weight: 600;
  }

  .run {
    display: flex;
    flex-direction: column;
    gap: 5px;
    width: 190px;
  }

  .run .btn {
    width: 100%;
  }

  .progress {
    height: 5px;
    border-radius: 3px;
    background: var(--grid-header);
    overflow: hidden;
  }

  .progress div {
    height: 100%;
    background: var(--accent);
    transition: width 0.15s linear;
  }

  .page {
    flex: 1;
    padding: 18px 20px 28px;
    display: flex;
    flex-direction: column;
    gap: 16px;
  }

  .notice {
    position: relative;
  }

  .dismiss {
    position: absolute;
    top: 6px;
    right: 8px;
    border: 0;
    background: none;
    color: var(--muted);
    cursor: pointer;
    padding: 4px;
  }

  .error {
    color: var(--bad);
    font-weight: 600;
  }

  .statusbar {
    position: sticky;
    bottom: 0;
    display: flex;
    justify-content: space-between;
    gap: 16px;
    padding: 6px 20px;
    background: var(--card);
    border-top: 1px solid var(--border);
    color: var(--muted);
    font-size: 12px;
  }

  .statusbar .warn {
    color: var(--warn);
  }

  .sim-line {
    white-space: nowrap;
  }

  .tabbar,
  .sheet,
  .scrim {
    display: none;
  }

  @media (max-width: 1240px) {
    .subtitle {
      display: none;
    }

    h1 {
      font-size: 18px;
    }

    .chips {
      gap: 10px;
    }

    .chip {
      min-width: 82px;
    }
  }

  @media (max-width: 900px) {
    .shell {
      display: block;
    }

    .sidebar,
    .statusbar {
      display: none;
    }

    .mobile-logo {
      display: inline-grid;
    }

    .topbar {
      flex-wrap: wrap;
      gap: 8px 12px;
      padding: 10px 14px;
      padding-top: max(10px, env(safe-area-inset-top));
    }

    h1 {
      font-size: 18px;
    }

    .run {
      width: auto;
      min-width: 150px;
    }

    .chips {
      order: 3;
      width: 100%;
      display: grid;
      grid-template-columns: repeat(4, minmax(0, 1fr));
      gap: 6px;
    }

    .chip {
      min-width: 0;
    }

    .chip span {
      font-size: 10px;
      white-space: nowrap;
      overflow: hidden;
      text-overflow: ellipsis;
    }

    .chip b {
      font-size: 15px;
    }

    .page {
      padding: 12px 12px calc(76px + env(safe-area-inset-bottom));
      gap: 12px;
    }

    .tabbar {
      display: grid;
      grid-template-columns: repeat(5, 1fr);
      position: fixed;
      left: 0;
      right: 0;
      bottom: 0;
      z-index: 6;
      background: var(--nav-bg);
      padding-bottom: env(safe-area-inset-bottom);
    }

    .tabbar button {
      border: 0;
      background: none;
      color: var(--nav-text);
      padding: 8px 2px 9px;
      display: flex;
      flex-direction: column;
      align-items: center;
      gap: 2px;
      font-size: 12px;
      cursor: pointer;
    }

    .tabbar button.active {
      color: #fff;
      box-shadow: inset 0 3px 0 var(--nav-active);
    }

    .tabbar .glyph {
      font-size: 17px;
    }

    .scrim {
      display: block;
      position: fixed;
      inset: 0;
      z-index: 6;
      border: 0;
      background: rgb(10 18 25 / 35%);
    }

    .sheet {
      display: flex;
      flex-direction: column;
      position: fixed;
      left: 0;
      right: 0;
      bottom: calc(58px + env(safe-area-inset-bottom));
      z-index: 7;
      background: var(--nav-bg);
      padding: 8px 0;
      border-radius: 12px 12px 0 0;
    }

    .sheet button {
      display: flex;
      align-items: center;
      gap: 14px;
      border: 0;
      background: none;
      color: var(--nav-text);
      padding: 12px 20px;
      font-size: 15px;
      text-align: left;
      cursor: pointer;
    }

    .sheet button.active {
      color: #fff;
    }

    .sheet hr {
      border: 0;
      border-top: 1px solid var(--nav-hover);
      margin: 6px 0;
    }
  }
</style>
