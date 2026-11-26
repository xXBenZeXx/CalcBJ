// Everything the pages share: settings, the results kept per configuration, starting/stopping simulations and
// scenario files. Like the Windows app, simulations only run when the user presses Run; settings are saved in the
// browser.
import { engine } from "./engine/engine";
import type { BetRamp, CountSystem, Evaluation, Rules, SimData, StrategyConfig } from "./engine/types";
import { NUM_BINS } from "./engine/wasm";
import { countLabel, trim } from "./format";
import type { CompareEntry, Scenario } from "./scenario";
import { rampRow, resizeRamp } from "./sim-data";
import { clampThreads, defaultThreads, maxThreads, simulation } from "./simulation.svelte";
import { countText, isBalanced, strategyText } from "./text";

/** The most simulation threads this device may use (the upper limit of the threads setting). */
export { maxThreads };

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
/** Rounds per Run unless the user chose otherwise: a phone simulates several times slower than a computer. */
const DEFAULT_SIM_ROUNDS = __MOBILE__ ? 50e6 : 200e6;

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

/** "full" when the storage quota is used up, "failed" when storage is blocked or missing. */
type WriteResult = "ok" | "full" | "failed";

function write(key: string, text: string): WriteResult {
  try {
    localStorage.setItem(key, text);
    return "ok";
  } catch (e) {
    // Browsers name the quota error differently (Firefox had its own name; 22 is the old numeric code).
    const full =
      e instanceof DOMException &&
      (e.name === "QuotaExceededError" || e.name === "NS_ERROR_DOM_QUOTA_REACHED" || e.code === 22);
    return full ? "full" : "failed";
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

/**
 * A results key ends with the engine's results version ("#results=2", see RESULTS_VERSION in web/engine): a version
 * whose simulations give other results gets a new number. Keys stored by CalcBJ up to 0.1.6 have none (version 0).
 */
const VERSION_MARK = "#results=";

/** The settings part of a results key, without its version. */
function settingsOf(key: string): string {
  const at = key.lastIndexOf(VERSION_MARK);
  return at < 0 ? key : key.slice(0, at);
}

/** The engine results version a key was stored with; 0 when it has none. */
function versionOf(key: string): number {
  const at = key.lastIndexOf(VERSION_MARK);
  return at < 0 ? 0 : Number(key.slice(at + VERSION_MARK.length)) || 0;
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

/**
 * Brings rules from a file or from storage into the ranges the program works with, as the Windows app does: 1 to 8
 * decks, split to at most 4 hands, a blackjack payout in twentieths of a unit (what the simulator pays in) and a
 * penetration from 0 to 100%.
 */
export function sanitizeRules(r: Rules): Rules {
  const int = (v: number, min: number, max: number, or: number) =>
    Number.isFinite(v) ? Math.max(min, Math.min(max, Math.round(v))) : or;
  const pays = Number.isFinite(r.blackjackPays) ? Math.max(0, Math.min(10, r.blackjackPays)) * 20 : 30;
  // Ties to even, as .NET rounds.
  const twentieths = Math.abs(pays % 1) === 0.5 ? 2 * Math.round(pays / 2) : Math.round(pays);
  return {
    ...r,
    decks: int(r.decks, 1, 8, 6),
    maxSplitHands: int(r.maxSplitHands, 1, 4, 4),
    blackjackPays: twentieths / 20,
    penetration: Number.isFinite(r.penetration) ? Math.max(0, Math.min(1, r.penetration)) : 0.75,
  };
}

/** Whether a count value means another true count when the decks or the penetration change (C#: DependsOnDecks). */
export function dependsOnDecks(cs: CountSystem): boolean {
  return cs.metric === "RunningCount" || !isBalanced(cs) || cs.ircBase !== 0 || cs.ircPerDeck !== 0;
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
  simRounds = $state(DEFAULT_SIM_ROUNDS);
  #threads = $state(defaultThreads);
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
  /**
   * Why settings or results could not be stored on this device (storage full or blocked), for the interface to show;
   * null while everything is saved.
   */
  storageError = $state<string | null>(null);

  private cache = new Map<string, SimData>();
  private restartPending = false;
  /** Counts "Discard results & re-run": a run that started before a discard must not bring its results back. */
  private epoch = 0;
  private settingsSaved: WriteResult = "ok";
  private simsSaved: WriteResult = "ok";

  /** Simulation threads, always within what this device allows (see maxThreads). */
  get threads(): number {
    return this.#threads;
  }

  set threads(n: number) {
    this.#threads = clampThreads(n);
  }

  rph = $derived(this.autoRph ? RPH_TABLE[Math.max(1, Math.min(7, this.spots))] : this.manualRph);

  simKey = $derived.by(() => {
    try {
      return engine.simKey(this.rules, this.count, this.strategy, this.spots);
    } catch {
      return "";
    }
  });

  simStale = $derived(this.sim !== null && this.sim.key !== this.simKey);

  /**
   * True when the results on screen are of these settings but were simulated by an older version of CalcBJ, whose
   * engine gave other results (an older results version in the key). They are stale too, and Run starts from zero.
   */
  simOutdated = $derived(
    this.sim !== null &&
      this.simKey !== "" &&
      settingsOf(this.sim.key) === settingsOf(this.simKey) &&
      versionOf(this.sim.key) < versionOf(this.simKey),
  );

  /**
   * True when the simulation shown was run with another count (or the same count converted differently): its count
   * values then aren't the ones the bet ramp is set for, so the ramp can't be worked out on it.
   */
  simOfAnotherCount = $derived.by(() => {
    const key = this.sim?.key;
    if (!key) return false;
    try {
      // The key is rules # count # strategy # spots; the rules part has no "#".
      const at = key.indexOf("#");
      return at < 0 || !key.slice(at + 1).startsWith(engine.countKey(this.count) + "#");
    } catch {
      return true;
    }
  });

  evaluation = $derived.by((): Evaluation | null => {
    const d = this.sim;
    if (!d || this.simOfAnotherCount) return null;
    try {
      return engine.evaluate(d, this.ramp, this.unit, this.rph, this.bankroll);
    } catch {
      return null;
    }
  });

  constructor() {
    this.restore();
    // The phone apps keep everything in the WebView's storage, which iOS may clear when the phone runs short of space
    // unless the app's storage is persistent. The system decides whether to grant it; nothing else depends on it.
    if (__MOBILE__) void navigator.storage?.persist?.().catch(() => false);
  }

  // ---------------- settings that move other settings ----------------

  /**
   * Moves the ramp to the current count and rules from the ones it was set for, as the Windows app does: a preset is
   * applied again over the new range; a custom ramp keeps its bets at the same true counts.
   */
  carryRamp(oldCount: CountSystem, oldRules: Rules): void {
    const r = engine.carryRamp(oldRules, oldCount, this.rules, this.count, this.ramp, this.rampPreset);
    this.ramp = r.ramp;
    this.rampPreset = r.preset;
  }

  resetIndexRange(): void {
    const range = engine.indexRange(this.rules, this.count);
    this.strategy.indexMin = range.min;
    this.strategy.indexMax = range.max;
  }

  /** After another count was chosen, or its values came to mean other true counts (metric, starting count). */
  countChanged(old: CountSystem): void {
    this.carryRamp(old, this.rules);
    this.resetIndexRange();
  }

  /**
   * After the decks or the penetration changed: for a running count, an unbalanced count or one with a starting count,
   * count values mean other true counts now. With other decks the ramp moves with them and the index range is set
   * anew; with another penetration only a preset follows, and a custom ramp stays as it was set.
   */
  rulesChanged(old: Rules): void {
    if (!dependsOnDecks(this.count)) return;
    if (old.decks !== this.rules.decks) {
      this.carryRamp(this.count, old);
      this.resetIndexRange();
    } else if (old.penetration !== this.rules.penetration && this.rampPreset < CUSTOM_PRESET) {
      this.carryRamp(this.count, old);
    }
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
    this.epoch++;
    this.cache.clear();
    this.saveSims();
    this.sim = null;
    this.run();
  }

  /**
   * Shows the stored results for these settings, if any (called whenever the settings change); results an older
   * version simulated for them only when there are no current ones (they are shown as outdated).
   */
  showCached(key: string): void {
    const cached = this.cache.get(key) ?? this.outdatedFor(key);
    if (cached && this.runningKey !== key && this.sim !== cached) this.sim = cached;
  }

  /** The newest results an older version of CalcBJ simulated for the settings of `key`, if any are held. */
  private outdatedFor(key: string): SimData | undefined {
    const settings = settingsOf(key);
    const version = versionOf(key);
    let found: SimData | undefined;
    for (const d of this.cache.values()) {
      const v = versionOf(d.key);
      if (settingsOf(d.key) === settings && v < version && (!found || v > versionOf(found.key))) found = d;
    }
    return found;
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
    const epoch = this.epoch;
    const request = { rules, count, config, spots: this.spots, rounds: this.simRounds, threads: this.threads };
    simulation.start(request, base, labels, (data, phase) => {
      const finished = phase === "finished";
      // After "Discard results" the run's results still contain the discarded ones: they are dropped too.
      const kept = data !== null && epoch === this.epoch;
      if (finished) this.runningKey = null;
      // A checkpoint and the end are both `base` plus the run so far, so each replaces the one before it (the
      // checkpoint was only there in case the app was closed or killed first).
      if (kept && phase !== "progress") this.remember(data);
      // Results for settings that are no longer selected only stay on screen if nothing is held for the new ones.
      if (kept && phase !== "checkpoint" && (key === this.simKey || !this.cache.has(this.simKey))) this.sim = data;
      if (finished && this.restartPending) {
        this.restartPending = false;
        this.start();
      }
    });
  }

  /**
   * Keeps results for their settings (the larger run wins); sweeps store theirs here too. Of results that different
   * versions of CalcBJ simulated for the same settings only the newest version's are kept, whatever their rounds.
   */
  remember(d: SimData): void {
    if (!d.key || d.rounds <= 0) return;
    const settings = settingsOf(d.key);
    const version = versionOf(d.key);
    for (const other of [...this.cache.values()]) {
      if (other.key === d.key || settingsOf(other.key) !== settings) continue;
      if (versionOf(other.key) > version) return;
      this.cache.delete(other.key);
    }
    const old = this.cache.get(d.key);
    if (old && old.rounds > d.rounds) return;
    this.cache.delete(d.key);
    this.cache.set(d.key, d);
    while (this.cache.size > CACHE_SIZE) this.cache.delete(this.cache.keys().next().value as string);
    this.saveSims();
  }

  /**
   * Stores the results held, oldest first, without the `drop` oldest. When storage is full, more of the oldest are
   * left out until the rest fit, but never the newest. Those left out are given up here too once the write worked,
   * so that storage and memory agree; when nothing fits, everything stays in memory for this visit.
   */
  private saveSims(drop = 0): WriteResult {
    const entries = [...this.cache.values()];
    // The JSON of an array is its items' JSON between brackets, so each result is turned into text only once.
    const texts = entries.map((d) => JSON.stringify(d));
    let dropped = Math.min(drop, texts.length);
    let result = write(SIMS_KEY, `[${texts.slice(dropped).join(",")}]`);
    while (result === "full" && dropped < texts.length - 1) {
      dropped++;
      result = write(SIMS_KEY, `[${texts.slice(dropped).join(",")}]`);
    }
    if (result === "ok") for (const d of entries.slice(0, dropped)) this.cache.delete(d.key);
    this.cacheCount = this.cache.size;
    this.simsSaved = result;
    this.storageChanged();
    return result;
  }

  /** Sets `storageError` from the outcome of the last writes. */
  private storageChanged(): void {
    const settings = this.settingsSaved !== "ok";
    const sims = this.simsSaved !== "ok";
    if (!settings && !sims) {
      this.storageError = null;
      return;
    }
    const full = this.settingsSaved === "full" || this.simsSaved === "full";
    const what = settings && sims ? "your settings and results" : settings ? "your settings" : "the newest results";
    this.storageError =
      (full ? "This device's storage is full" : "Storage is blocked here") +
      `, so CalcBJ could not save ${what}. They last until CalcBJ is closed; Save scenario keeps the current game ` +
      "and its results in a file.";
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
    if (this.simStale && this.sim?.rulesText) {
      // The results are of the last simulation, not of the settings as they are now: say what was simulated.
      e.rules = this.sim.rulesText;
      e.count = this.sim.countText;
      e.strategy = this.sim.strategyText;
      if (Number.isFinite(this.sim.basicEv)) e.houseEdge = -this.sim.basicEv;
    }
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
    const rules = sanitizeRules({ ...defaultRules(), ...s.rules });
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
    // The count was corrected on the way in: its ramp and index range were set for the old tags.
    if (count !== s.count) this.countChanged(s.count);
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
    const text = JSON.stringify(s);
    let result = write(SETTINGS_KEY, text);
    // The settings matter more than old results: when storage is full, those are given up, oldest first, as long as
    // that makes room.
    while (result === "full" && this.cache.size > 0 && this.saveSims(1) === "ok") result = write(SETTINGS_KEY, text);
    this.settingsSaved = result;
    // Results that could not be stored before (storage was full) are tried again, now that there may be room.
    if (this.simsSaved !== "ok") this.saveSims();
    else this.storageChanged();
  }

  private restore(): void {
    const s = read<Saved>(SETTINGS_KEY);
    if (s?.version === 1) {
      try {
        const rules = sanitizeRules({ ...defaultRules(), ...s.rules });
        const saved = { ...structuredClone(presets[0]), ...s.count };
        const count = upgradeSavedCount(saved);
        const strategy = { ...defaultStrategy(), ...s.strategy };
        // An index range left the wrong way round or beyond the count bins (an extreme starting count could do that).
        const [lo, hi] = [Math.min(strategy.indexMin, strategy.indexMax), Math.max(strategy.indexMin, strategy.indexMax)];
        strategy.indexMin = Math.max(-100, Math.min(100, lo));
        strategy.indexMax = Math.max(-100, Math.min(100, hi));
        const spots = number(s.spots, 1, 7, 3);
        engine.simKey(rules, count, strategy, spots); // throws on settings the engine rejects
        this.rules = rules;
        this.count = count;
        this.strategy = strategy;
        this.spots = spots;
        if (validRamp(s.ramp)) this.ramp = s.ramp;
        else this.ramp = engine.defaultRamp(rules, count, null, 3).ramp;
        this.rampPreset = number(s.rampPreset, 0, CUSTOM_PRESET, 3);
        if (count !== saved) this.countChanged(saved);
      } catch {
        // Keep the defaults.
      }
      this.autoRph = s.autoRph !== false;
      this.manualRph = number(s.manualRph, 10, 400, 105);
      this.unit = number(s.unit, 0.01, 1e6, 25);
      this.bankroll = number(s.bankroll, 1, 1e9, 25000);
      this.simRounds = ROUND_OPTIONS.includes(s.simRounds) ? s.simRounds : DEFAULT_SIM_ROUNDS;
      // The setter keeps it within what this device allows.
      this.threads = number(s.threads, 1, 64, defaultThreads);
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
    this.sim = this.cache.get(this.simKey) ?? this.outdatedFor(this.simKey) ?? null;
  }
}

export const app = new AppState();
