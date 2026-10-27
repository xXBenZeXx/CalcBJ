// Runs simulations on all cores: one Web Worker per thread, each with its own engine and shoe. Like the Windows
// app, workers take chunks of rounds until the target is reached; a stop lets the chunks in progress finish.
import { engineModule } from "./engine/engine";
import type { WorkerReply, WorkerRequest } from "./engine/sim-worker";
import type { CountMetric, CountSystem, Rules, SimData, StrategyConfig } from "./engine/types";
import { BIN_MIN, NUM_BINS, TALLY_LEN } from "./engine/wasm";
import { mergeSim } from "./sim-data";

/** Rounds per worker request. */
const CHUNK = 500_000;
/** How often running results are passed on, in milliseconds. */
const UPDATE_MS = 150;

export interface SimRequest {
  rules: Rules;
  count: CountSystem;
  config: StrategyConfig;
  spots: number;
  rounds: number;
  threads: number;
}

export interface SimLabels {
  key: string;
  rulesText: string;
  countText: string;
  strategyText: string;
  metric: CountMetric;
}

/** Receives the results so far (merged with `base`); `data` is null when the run ended before any round. */
export type SimListener = (data: SimData | null, finished: boolean) => void;

type WorkerState = "preparing" | "idle" | "running" | "failed";

function randomSeed(): string {
  const seed = new BigUint64Array(1);
  crypto.getRandomValues(seed);
  return seed[0].toString();
}

class Simulation {
  running = $state(false);
  done = $state(0);
  target = $state(0);
  seconds = $state(0);
  error = $state<string | null>(null);
  /** Set when Stop ended the last run early (a sweep then stops too). */
  stoppedByUser = false;

  private pool: Worker[] = [];
  private job = 0;
  private stopping = false;

  stop(): void {
    if (!this.running) return;
    this.stopping = true;
    this.stoppedByUser = true;
  }

  /** Runs a fresh simulation to the end (or until stopped) and resolves with its results. */
  run(req: SimRequest, labels: SimLabels): Promise<SimData | null> {
    return new Promise((resolve) =>
      this.start(req, null, labels, (data, finished) => {
        if (finished) resolve(data);
      }),
    );
  }

  start(req: SimRequest, base: SimData | null, labels: SimLabels, listener: SimListener): void {
    if (this.running) throw new Error("A simulation is already running.");
    const threads = Math.max(1, Math.min(64, Math.round(req.threads)));
    this.resize(threads);
    const job = ++this.job;
    const seed = randomSeed();
    const tally = new Float64Array(TALLY_LEN);
    const states: WorkerState[] = new Array(threads).fill("preparing");
    const started = performance.now();
    let claimed = 0;
    let basicEv = Number.NaN;
    let timer = 0;

    this.stopping = false;
    this.stoppedByUser = false;
    this.running = true;
    this.done = 0;
    this.target = req.rounds;
    this.seconds = 0;
    this.error = null;

    const results = (): SimData | null =>
      this.done > 0 ? mergeSim(base, this.build(tally, req.spots, threads, basicEv, labels)) : base;

    const finishIfIdle = () => {
      if (states.some((s) => s === "preparing" || s === "running")) return;
      clearTimeout(timer);
      this.seconds = (performance.now() - started) / 1000;
      this.running = false;
      listener(results(), true);
    };

    const assign = (i: number) => {
      if (!this.stopping && claimed < req.rounds) {
        const rounds = Math.min(CHUNK, req.rounds - claimed);
        claimed += rounds;
        states[i] = "running";
        this.pool[i].postMessage({ type: "run", job, rounds } satisfies WorkerRequest);
      } else {
        states[i] = "idle";
        finishIfIdle();
      }
    };

    const fail = (i: number, message: string) => {
      this.error = message;
      this.stopping = true;
      states[i] = "failed";
      finishIfIdle();
    };

    this.pool.slice(0, threads).forEach((w, i) => {
      w.onmessage = (e: MessageEvent<WorkerReply>) => {
        const m = e.data;
        if (m.job !== job) return;
        if (m.type === "prepared") {
          basicEv = m.basicEv;
          assign(i);
        } else if (m.type === "tally") {
          for (let k = 0; k < TALLY_LEN; k++) tally[k] += m.tally[k];
          this.done += m.rounds;
          this.seconds = (performance.now() - started) / 1000;
          timer ||= setTimeout(() => {
            timer = 0;
            if (this.job === job && this.running) listener(results(), false);
          }, UPDATE_MS);
          assign(i);
        } else {
          fail(i, m.message);
        }
      };
      w.onerror = (e) => {
        e.preventDefault();
        if (this.job === job && states[i] !== "failed") fail(i, "A simulation worker stopped: " + (e.message || "unknown error"));
      };
      const request = { ...req, threads: undefined, rounds: undefined, seed, worker: i };
      w.postMessage({ type: "prepare", job, request } satisfies WorkerRequest);
    });
  }

  private build(tally: Float64Array, spots: number, threads: number, basicEv: number, labels: SimLabels): SimData {
    const column = (k: number) => Array.from(tally.subarray(k * NUM_BINS, (k + 1) * NUM_BINS));
    return {
      binMin: BIN_MIN,
      numBins: NUM_BINS,
      spots,
      n: column(0),
      sa: column(1),
      qa: column(2),
      sb: column(3),
      qb: column(4),
      ab: column(5),
      rounds: tally[6 * NUM_BINS],
      shoes: tally[6 * NUM_BINS + 1],
      emergency: tally[6 * NUM_BINS + 2],
      seconds: this.seconds,
      threads,
      ...labels,
      basicEv,
      created: Date.now(),
    };
  }

  /** Keeps `n` workers; new ones get the compiled engine. */
  private resize(n: number): void {
    while (this.pool.length < n) {
      const w = new Worker(new URL("./engine/sim-worker.ts", import.meta.url), { type: "module" });
      w.postMessage({ type: "init", module: engineModule() } satisfies WorkerRequest);
      this.pool.push(w);
    }
    while (this.pool.length > n) this.pool.pop()?.terminate();
  }
}

export const simulation = new Simulation();
