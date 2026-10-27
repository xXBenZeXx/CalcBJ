// Sensitivity sweep: re-simulates the game over a range of one parameter, or re-optimises the ramp for each maximum
// bet (as in the Windows app). Lives outside the page so a sweep keeps running when you look at other pages.
import { engine } from "./engine/engine";
import type { AnalysisResult, IndexMode, OptimizerSettings } from "./engine/types";
import { trim } from "./format";
import { simulation } from "./simulation.svelte";
import { RPH_TABLE, app } from "./state.svelte";
import { countText, strategyText } from "./text";

export const SWEEP_PARAMETERS = [
  "Penetration (%)",
  "Number of decks",
  "Spots per round",
  "Index set",
  "Max bet, optimal ramp (no re-sim)",
];
export const SWEEP_ROUNDS = [10e6, 25e6, 50e6, 100e6, 200e6];
export const SWEEP_METRICS = [
  "SCORE",
  "Win rate per hour",
  "Player edge",
  "Risk of ruin",
  "N0 (hours)",
  "Std. deviation per hour",
];

const NOTES = [
  "Each point is a fresh simulation at that penetration.",
  "Each point is a fresh simulation with that many decks (index plays are regenerated).",
  "More spots means fewer rounds per hour (auto table) and fewer rounds per shoe.",
  "0 = basic strategy, 1 = Illustrious 18, 2 = I18 + Fab 4, 3 = full indices.",
  "Uses the current simulation; for each maximum bet the SCORE-optimal ramp (min 1 unit, whole units) is found.",
];
/** From, to, step and decimals for each parameter. */
const DEFAULTS = [
  [60, 90, 5, 1],
  [1, 8, 1, 0],
  [1, 7, 1, 0],
  [0, 3, 1, 0],
  [2, 24, 2, 0],
];
const INDEX_MODES: IndexMode[] = ["BasicOnly", "Illustrious18", "I18Fab4", "FullIndices"];
const INDEX_NAMES = ["Basic", "I18", "I18+Fab4", "Full"];

export interface SweepPoint {
  label: string;
  value: number;
  result: AnalysisResult | null;
}

class Sweep {
  parameter = $state(0);
  from = $state(60);
  to = $state(90);
  step = $state(5);
  decimals = $state(1);
  rounds = $state(50e6);
  metric = $state(0);
  running = $state(false);
  points = $state<SweepPoint[]>([]);
  status = $state("");

  private cancel = false;

  note = $derived(NOTES[this.parameter]);

  setParameter(p: number): void {
    this.parameter = p;
    [this.from, this.to, this.step, this.decimals] = DEFAULTS[p];
  }

  stop(): void {
    this.cancel = true;
    simulation.stop();
  }

  async run(): Promise<void> {
    if (this.running || simulation.running) return;
    const p = this.parameter;
    if (!(this.step > 0) || this.to < this.from) {
      this.status = "Check the range.";
      return;
    }
    const sim = app.sim;
    if (p === 4 && !sim) {
      this.status = "Run a simulation first.";
      return;
    }
    const values: number[] = [];
    for (let v = this.from; v <= this.to + 1e-9 && values.length < 60; v += this.step) values.push(Math.round(v * 1e4) / 1e4);

    const rules = $state.snapshot(app.rules);
    const count = $state.snapshot(app.count);
    const config = $state.snapshot(app.strategy);
    const ramp = $state.snapshot(app.ramp);
    const { spots, unit, rph, bankroll, autoRph, threads } = app;
    const rounds = this.rounds;

    this.points = [];
    this.cancel = false;
    this.running = true;
    this.status = `Running ${values.length} point(s)…`;
    try {
      for (const v of values) {
        if (this.cancel) break;
        let point: SweepPoint;
        if (p === 4 && sim) {
          const os: OptimizerSettings = {
            minUnits: 1,
            maxUnits: Math.max(1, v),
            increment: 1,
            maxHands: 1,
            allowWongOut: false,
            kelly: false,
            kellyFraction: 1,
            bankrollUnits: 1000,
          };
          const best = engine.optimize(sim, ramp, os);
          point = { label: "1–" + trim(v, 2), value: v, result: engine.evaluate(sim, best, unit, rph, bankroll).result };
        } else {
          const r = { ...rules };
          const sc = { ...config, overrides: [...config.overrides] };
          let sp = spots;
          let pointRph = rph;
          let label: string;
          if (p === 0) {
            r.penetration = v / 100;
            label = trim(v, 1) + "%";
          } else if (p === 1) {
            r.decks = Math.max(1, Math.min(8, Math.trunc(v)));
            label = r.decks + "D";
          } else if (p === 2) {
            sp = Math.max(1, Math.min(7, Math.trunc(v)));
            if (autoRph) pointRph = RPH_TABLE[sp];
            label = sp + " spots";
          } else {
            const m = Math.max(0, Math.min(3, Math.trunc(v)));
            sc.mode = INDEX_MODES[m];
            label = INDEX_NAMES[m];
          }
          const labels = {
            key: engine.simKey(r, count, sc, sp),
            rulesText: engine.rulesInfo(r, sp).describe,
            countText: countText(count),
            strategyText: strategyText(sc),
            metric: count.metric,
          };
          const d = await simulation.run({ rules: r, count, config: sc, spots: sp, rounds, threads }, labels);
          if (simulation.error) throw new Error(simulation.error);
          if (simulation.stoppedByUser) this.cancel = true;
          if (!d || d.rounds === 0) break;
          if (!this.cancel) app.remember(d);
          point = { label, value: v, result: engine.evaluate(d, ramp, unit, pointRph, bankroll).result };
        }
        this.points.push(point);
        this.status = `Point ${this.points.length} of ${values.length} done.`;
      }
      if (this.cancel) this.status = `Stopped after ${this.points.length} point(s).`;
    } catch (e) {
      this.status = "Sweep failed: " + (e instanceof Error ? e.message : String(e));
    } finally {
      this.running = false;
    }
  }
}

export const sweep = new Sweep();
