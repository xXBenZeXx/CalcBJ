// Everything the pages share: settings, the results kept per configuration, starting/stopping simulations and
// scenario files. Like the Windows app, simulations only run when the user presses Run; settings are saved in the
// browser.
import { engine } from "./engine/engine";
import type { BetRamp, CountSystem, Evaluation, Rules, SimData, StrategyConfig } from "./engine/types";
import { NUM_BINS } from "./engine/wasm";
import { countLabel, trim } from "./format";
import type { CompareEntry, Scenario } from "./scenario";
import { rampRow, resizeRamp } from "./sim-data";
import { simulation } from "./simulation.svelte";
import { countText, isBalanced, strategyText } from "./text";

export type PageId = "game" | "count" | "sim" | "results" | "risk" | "charts" | "sweep" | "compare" | "guide";
export const PAGE_IDS: PageId[] = ["game", "count", "sim", "results", "risk", "charts", "sweep", "compare", "guide"];

/** Rounds per hour for 1..7 spots. */
export const RPH_TABLE = [0, 209, 139, 105, 84, 70, 60, 52];
export const ROUND_OPTIONS = [10e6, 25e6, 50e6, 100e6, 200e6, 500e6, 1e9, 2e9, 5e9, 10e9];
export const RAMP_PRESETS = [
  "Flat bet (1 unit)",
  "1–4 spread",
  "1–8 spread",
  "1–12 spread",
  "1–16 spread",
  "1–24 spread",
  "1–12, wong out at TC ≤ −1",
  "Back-count: play TC ≥ +1 (2–12)",
  "(custom ramp)",
];
export const CUSTOM_PRESET = 8;

export const presets: CountSystem[] = engine.presets();

const SETTINGS_KEY = "calcbj.settings.v1";
const SIMS_KEY = "calcbj.sims.v1";
const CACHE_SIZE = 40;

export function defaultRules(): Rules {
  return {
    decks: 6,
    h17: true,
    holeCard: "USPeek",
    blackjackPays: 1.5,
    doubleOn: "AnyTwo",
    das: true,
    maxSplitHands: 4,
    resplitAces: false,
    hitSplitAces: false,
    surrender: "Late",
    insurance: true,
    penetration: 0.75,
    burnCard: true,
  };
}

export function defaultStrategy(): StrategyConfig {
  return { mode: "I18Fab4", indexMin: -10, indexMax: 10, overrides: [] };
}

interface Saved {
  version: 1;
  rules: Rules;
  count: CountSystem;
  strategy: StrategyConfig;
  spots: number;
  autoRph: boolean;
  manualRph: number;
  unit: number;
  bankroll: number;
  ramp: BetRamp;
  rampPreset: number;
  simRounds: number;
  threads: number;
  tripHours: number;
  tripBankroll: number;
  compare: CompareEntry[];
  page: PageId;
}

function read<T>(key: string): T | null {
  try {
    const text = localStorage.getItem(key);
    return text ? (JSON.parse(text) as T) : null;
  } catch {
    return null;
  }
}

function write(key: string, value: unknown): void {
  try {
    localStorage.setItem(key, JSON.stringify(value));
  } catch {
    // Storage blocked or full: settings then last only for this visit.
  }
}

function number(v: unknown, min: number, max: number, fallback: number): number {
  return typeof v === "number" && Number.isFinite(v) && v >= min && v <= max ? v : fallback;
}

function validRamp(r: BetRamp | null | undefined): r is BetRamp {
  const rows = r ? r.max - r.min + 1 : 0;
  return !!r && rows >= 1 && r.units?.length === rows && r.hands?.length === rows;
}

function validSim(d: SimData | null | undefined): d is SimData {
  return !!d && d.rounds > 0 && [d.n, d.sa, d.qa, d.sb, d.qb, d.ab].every((a) => a?.length === NUM_BINS);
}

/** The result fields of a Compare entry; an entry without a valid result has them all NaN. */
const RESULT_FIELDS = [
  "winPerHour", "sdPerHour", "winPer100", "sdPer100", "edgePct", "score", "di", "n0", "hoursN0", "ror", "avgBet",
  "spread", "playedPct", "cePerHour", "bankroll5", "kellyUnit",
] as const;

/**
 * JSON keeps NaN and Infinity as null: an entry without results (null SCORE) gets its NaNs back, any other entry its
 * infinite N0 and bankroll (a losing game).
 */
function restoreEntry(e: CompareEntry): CompareEntry {
  const out = { ...e };
  if ((e.score as number | null) === null) for (const f of RESULT_FIELDS) out[f] = NaN;
  else for (const f of ["n0", "hoursN0", "bankroll5"] as const) if ((e[f] as number | null) === null) out[f] = Infinity;
  return out;
}

/** Counts saved before 0.1.1 can carry Canfield Expert's old tags (the 2 counted +1): brings them up to date. */
export function upgradeSavedCount(cs: CountSystem): CountSystem {
  const old = [0, 1, 1, 1, 1, 1, 1, 0, -1, -1];
  if (cs.name === "Canfield Expert" && !cs.redTags && cs.tags?.length === 10 && cs.tags.every((t, i) => t === old[i]))
    return { ...cs, tags: cs.tags.map((t, i) => (i === 1 ? 0 : t)) };
  return cs;
}

class AppState {
  rules = $state<Rules>(defaultRules());
  count = $state<CountSystem>(structuredClone(presets[0]));
  strategy = $state<StrategyConfig>(defaultStrategy());
  spots = $state(3);
  autoRph = $state(true);
  manualRph = $state(105);
  unit = $state(25);
  bankroll = $state(25000);
  ramp = $state<BetRamp>(engine.defaultRamp(defaultRules(), presets[0], null, 3).ramp);
  rampPreset = $state(3);
  simRounds = $state(200e6);
  threads = $state(Math.max(1, Math.min(64, navigator.hardwareConcurrency || 4)));
  tripHours = $state(40);
  tripBankroll = $state(10000);
  compare = $state<CompareEntry[]>([]);
  page = $state<PageId>("game");
  /** Name used when the scenario is saved (that of the file last opened). */
  fileName = $state("scenario.bjev");
  /** Results on screen: those of the current settings, or of earlier settings while none are held for these. */
  sim = $state.raw<SimData | null>(null);
  /** Settings of the simulation that is running (null while a sweep runs its own simulations). */
  runningKey = $state<string | null>(null);
  cacheCount = $state(0);

  private cache = new Map<string, SimData>();
  private restartPending = false;

  rph = $derived(this.autoRph ? RPH_TABLE[Math.max(1, Math.min(7, this.spots))] : this.manualRph);

  simKey = $derived.by(() => {
    try {
      return engine.simKey(this.rules, this.count, this.strategy, this.spots);
    } catch {
      return "";
    }
  });

  simStale = $derived(this.sim !== null && this.sim.key !== this.simKey);

  evaluation = $derived.by((): Evaluation | null => {
    const d = this.sim;
    if (!d) return null;
    try {
      return engine.evaluate(d, this.ramp, this.unit, this.rph, this.bankroll);
    } catch {
      return null;
    }
  });

  constructor() {
    this.restore();
  }

  // ---------------- settings that move other settings ----------------

  /** After the count or its scale changed: ramp rows and index range follow the count, as in the Windows app. */
  resetRanges(): void {
    const r = engine.defaultRamp(this.rules, this.count, this.ramp, this.rampPreset);
    this.ramp = r.ramp;
    this.rampPreset = r.preset;
    const range = engine.indexRange(this.rules, this.count);
    this.strategy.indexMin = range.min;
    this.strategy.indexMax = range.max;
  }

  /** The number of decks moves running counts and unbalanced true counts. */
  decksChanged(): void {
    if (this.count.metric === "RunningCount" || !isBalanced(this.count)) this.resetRanges();
  }

  applyRampPreset(preset: number): void {
    const r = engine.applyPreset(this.rules, this.count, this.ramp, preset);
    this.ramp = r.ramp;
    this.rampPreset = r.preset;
  }

  setRampRange(min: number, max: number): void {
    if (max > min) this.ramp = resizeRamp(this.ramp, min, max);
  }

  setAutoRph(on: boolean): void {
    if (!on) this.manualRph = this.rph;
    this.autoRph = on;
  }

  setManualRph(v: number): void {
    this.manualRph = v;
    this.autoRph = false;
  }

  /** The ramp as text, e.g. "≤-3:1, +1:2, +2:4, ≥+6:12x2". */
  rampText(): string {
    const r = this.ramp;
    const parts: string[] = [];
    let last = NaN;
    let lastHands = -1;
    for (let v = r.min; v <= r.max; v++) {
      const u = r.units[rampRow(r, v)];
      const h = r.hands[rampRow(r, v)];
      if (u === last && h === lastHands) continue;
      parts.push(countLabel(v, r.min, r.max).replace(/ /g, "") + ":" + trim(u, 2) + (h > 1 ? "x" + h : ""));
      last = u;
      lastHands = h;
    }
    return parts.join(", ");
  }

  // ---------------- simulations ----------------

  /** Run (F5): adds rounds to the results held for these settings; restarts a run that is in progress. */
  run(): void {
    if (simulation.running) {
      // During a sweep, Run stops it rather than queueing a run behind it.
      if (this.runningKey !== null) this.restartPending = true;
      simulation.stop();
      return;
    }
    this.start();
  }

  stop(): void {
    this.restartPending = false;
    simulation.stop();
  }

  toggleRun(): void {
    if (simulation.running) this.stop();
    else this.start();
  }

  /** Throws away every stored result and simulates the current settings from scratch. */
  discardAndRun(): void {
    this.cache.clear();
    this.cacheCount = 0;
    write(SIMS_KEY, []);
    this.sim = null;
    this.run();
  }

  /** Shows the stored results for these settings, if any (called whenever the settings change). */
  showCached(key: string): void {
    const cached = this.cache.get(key);
    if (cached && this.runningKey !== key && this.sim !== cached) this.sim = cached;
  }

  private start(): void {
    const key = this.simKey;
    if (!key) return;
    const base = this.cache.get(key) ?? null;
    const rules = $state.snapshot(this.rules);
    const count = $state.snapshot(this.count);
    const config = $state.snapshot(this.strategy);
    const labels = {
      key,
      rulesText: engine.rulesInfo(rules, this.spots).describe,
      countText: countText(count),
      strategyText: strategyText(config),
      metric: count.metric,
    };
    this.runningKey = key;
    const request = { rules, count, config, spots: this.spots, rounds: this.simRounds, threads: this.threads };
    simulation.start(request, base, labels, (data, finished) => {
      if (finished) {
        this.runningKey = null;
        if (data) this.remember(data);
      }
      // Results for settings that are no longer selected only stay on screen if nothing is held for the new ones.
      if (data && (key === this.simKey || !this.cache.has(this.simKey))) this.sim = data;
      if (finished && this.restartPending) {
        this.restartPending = false;
        this.start();
      }
    });
  }

  /** Keeps results for their settings (the larger run wins); sweeps store theirs here too. */
  remember(d: SimData): void {
    if (!d.key || d.rounds <= 0) return;
    const old = this.cache.get(d.key);
    if (old && old.rounds > d.rounds) return;
    this.cache.delete(d.key);
    this.cache.set(d.key, d);
    while (this.cache.size > CACHE_SIZE) this.cache.delete(this.cache.keys().next().value as string);
    this.cacheCount = this.cache.size;
    write(SIMS_KEY, [...this.cache.values()]);
  }

  // ---------------- compare ----------------

  /** The current game, ramp and results as a Compare entry. */
  snapshot(name: string): CompareEntry {
    const ev = this.evaluation;
    const a = ev?.result.valid ? ev.result : null;
    const e: CompareEntry = {
      name,
      rules: engine.rulesInfo(this.rules, this.spots).describe,
      count: countText(this.count),
      strategy: strategyText(this.strategy),
      ramp: this.rampText(),
      spots: this.spots,
      unit: this.unit,
      roundsPerHour: this.rph,
      bankroll: this.bankroll,
      // No simulation, or a ramp that never bets: no results rather than zeros, which would look best.
      winPerHour: NaN,
      sdPerHour: NaN,
      winPer100: NaN,
      sdPer100: NaN,
      edgePct: NaN,
      score: NaN,
      di: NaN,
      n0: NaN,
      hoursN0: NaN,
      ror: NaN,
      avgBet: NaN,
      spread: NaN,
      playedPct: NaN,
      cePerHour: NaN,
      houseEdge: -engine.houseEdge(this.rules),
      bankroll5: NaN,
      kellyUnit: NaN,
      rounds: this.sim?.rounds ?? 0,
      stale: this.simStale,
    };
    if (a && ev && this.sim) {
      Object.assign(e, {
        winPerHour: a.winPerHour,
        sdPerHour: a.sdPerHour,
        winPer100: a.winPer100,
        sdPer100: a.sdPer100,
        edgePct: a.edgePct,
        score: a.score,
        di: a.di,
        n0: a.n0 ?? Infinity,
        hoursN0: a.hoursN0 ?? Infinity,
        ror: a.ror,
        avgBet: a.avgBetPlayed * this.unit,
        spread: a.spread,
        playedPct: a.playedFrac * 100,
        cePerHour: a.cePerHour,
        bankroll5: ev.bankrollFor[0] ?? Infinity,
        kellyUnit: a.kellyUnit,
        rounds: this.sim.rounds,
      });
    }
    return e;
  }

  // ---------------- scenario files ----------------

  toScenario(): Scenario {
    return {
      rules: $state.snapshot(this.rules),
      count: $state.snapshot(this.count),
      strategy: $state.snapshot(this.strategy),
      spots: this.spots,
      roundsPerHour: this.rph,
      autoRoundsPerHour: this.autoRph,
      unit: this.unit,
      bankroll: this.bankroll,
      ramp: $state.snapshot(this.ramp),
      rampPreset: this.rampPreset,
      simRounds: this.simRounds,
      threads: this.threads,
      tripHours: this.tripHours,
      tripBankroll: this.tripBankroll,
      sim: this.sim,
      compare: $state.snapshot(this.compare),
    };
  }

  /** Opens a scenario like the Windows app does; throws (and changes nothing) when the engine rejects its settings. */
  loadScenario(s: Scenario, fileName: string): void {
    const rules = { ...defaultRules(), ...s.rules };
    const count = upgradeSavedCount(s.count);
    const strategy = { ...defaultStrategy(), ...s.strategy };
    const spots = Math.max(1, Math.min(7, Math.round(s.spots)));
    engine.simKey(rules, count, strategy, spots);
    if (simulation.running) this.stop();
    this.rules = rules;
    this.count = count;
    this.strategy = strategy;
    this.spots = spots;
    this.autoRph = s.autoRoundsPerHour;
    this.manualRph = s.roundsPerHour > 0 ? Math.max(10, Math.min(400, s.roundsPerHour)) : RPH_TABLE[spots];
    this.unit = s.unit > 0 ? s.unit : 25;
    this.bankroll = s.bankroll > 0 ? s.bankroll : 25000;
    this.ramp = validRamp(s.ramp) ? s.ramp : engine.defaultRamp(rules, count, null, s.rampPreset).ramp;
    this.rampPreset = Math.max(0, Math.min(CUSTOM_PRESET, Math.round(s.rampPreset)));
    if (ROUND_OPTIONS.includes(s.simRounds)) this.simRounds = s.simRounds;
    // The thread count is a property of this device, not of the scenario (the file may come from a bigger PC).
    if (s.tripHours > 0) this.tripHours = s.tripHours;
    if (s.tripBankroll > 0) this.tripBankroll = s.tripBankroll;
    this.sim = validSim(s.sim) ? s.sim : null;
    if (this.sim) this.remember(this.sim);
    this.compare = s.compare;
    this.fileName = fileName;
  }

  // ---------------- persistence ----------------

  save(): void {
    const s: Saved = {
      version: 1,
      rules: $state.snapshot(this.rules),
      count: $state.snapshot(this.count),
      strategy: $state.snapshot(this.strategy),
      spots: this.spots,
      autoRph: this.autoRph,
      manualRph: this.manualRph,
      unit: this.unit,
      bankroll: this.bankroll,
      ramp: $state.snapshot(this.ramp),
      rampPreset: this.rampPreset,
      simRounds: this.simRounds,
      threads: this.threads,
      tripHours: this.tripHours,
      tripBankroll: this.tripBankroll,
      compare: $state.snapshot(this.compare),
      page: this.page,
    };
    write(SETTINGS_KEY, s);
  }

  private restore(): void {
    const s = read<Saved>(SETTINGS_KEY);
    if (s?.version === 1) {
      try {
        const rules = { ...defaultRules(), ...s.rules };
        const count = upgradeSavedCount({ ...structuredClone(presets[0]), ...s.count });
        const strategy = { ...defaultStrategy(), ...s.strategy };
        const spots = number(s.spots, 1, 7, 3);
        engine.simKey(rules, count, strategy, spots); // throws on settings the engine rejects
        this.rules = rules;
        this.count = count;
        this.strategy = strategy;
        this.spots = spots;
        if (validRamp(s.ramp)) this.ramp = s.ramp;
        else this.ramp = engine.defaultRamp(rules, count, null, 3).ramp;
        this.rampPreset = number(s.rampPreset, 0, CUSTOM_PRESET, 3);
      } catch {
        // Keep the defaults.
      }
      this.autoRph = s.autoRph !== false;
      this.manualRph = number(s.manualRph, 10, 400, 105);
      this.unit = number(s.unit, 0.01, 1e6, 25);
      this.bankroll = number(s.bankroll, 1, 1e9, 25000);
      this.simRounds = ROUND_OPTIONS.includes(s.simRounds) ? s.simRounds : 200e6;
      this.threads = number(s.threads, 1, 64, this.threads);
      this.tripHours = number(s.tripHours, 1, 1e5, 40);
      this.tripBankroll = number(s.tripBankroll, 1, 1e9, 10000);
      if (Array.isArray(s.compare)) this.compare = s.compare.map(restoreEntry);
      if (PAGE_IDS.includes(s.page)) this.page = s.page;
    }
    const sims = read<SimData[]>(SIMS_KEY);
    if (Array.isArray(sims)) {
      for (const d of sims) if (d?.key && validSim(d)) this.cache.set(d.key, d);
      this.cacheCount = this.cache.size;
    }
    this.sim = this.cache.get(this.simKey) ?? null;
  }
}

export const app = new AppState();
