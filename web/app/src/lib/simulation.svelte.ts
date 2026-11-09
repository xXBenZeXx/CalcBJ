// Runs simulations on all cores: one Web Worker per thread, each with its own engine and shoe. Like the Windows
// app, workers take chunks of rounds until the target is reached; a stop lets the chunks in progress finish (for a
// limited time). The results so far are also passed on as checkpoints, so that a closed or killed app keeps them.
import { engineModule } from "./engine/engine";
import type { WorkerReply, WorkerRequest } from "./engine/sim-worker";
import type { CountMetric, CountSystem, Rules, SimData, StrategyConfig } from "./engine/types";
import { BIN_MIN, NUM_BINS, TALLY_LEN } from "./engine/wasm";
import { mergeSim } from "./sim-data";

/** Rounds per worker request. */
const CHUNK = 500_000;
/** How often running results are passed on, in milliseconds. */
const UPDATE_MS = 150;
/** Running time between checkpoints: the results so far, kept in case the app is closed or killed before the end. */
const CHECKPOINT_MS = 60_000;
/**
 * How long a run waits for the chunks in progress once it hands out no more (after Stop, an error or the last chunk).
 * A chunk or a worker's set-up takes a few seconds at most, even on a slow phone; a worker that takes longer has hung
 * or died, and is replaced.
 */
const DRAIN_MS = 15_000;
/**
 * The phone apps pause a run while they are in the background: iOS suspends them there anyway, and Android would keep
 * the cores busy out of sight (and may close the app for it). The desktop and web apps keep simulating.
 */
const PAUSE_WHEN_HIDDEN = __MOBILE__;

/** Cores the browser reports: iPhones always say 4, Android counts its slow efficiency cores too. */
const cores = Math.max(1, Math.round(navigator.hardwareConcurrency || 4));
/**
 * Threads to simulate on unless the user chose otherwise. Phones use every core up to 4; with more, two are left for
 * the interface and the system and at most 6 run, because the extra cores are slow and each thread holds an engine.
 */
export const defaultThreads = __MOBILE__ ? (cores <= 4 ? cores : Math.min(cores - 2, 6)) : Math.min(64, cores);
/** The most threads this device may use: the cores it reports on a phone, 64 elsewhere. */
export const maxThreads = __MOBILE__ ? Math.max(cores, defaultThreads) : 64;

/** A thread count within 1..maxThreads (the default for anything that is not a number). */
export function clampThreads(n: number): number {
  return Number.isFinite(n) ? Math.max(1, Math.min(maxThreads, Math.round(n))) : defaultThreads;
}

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

/**
 * Receives the results so far, merged with `base` (null when there are none): "progress" while the run goes on, for
 * the screen; "checkpoint" to keep them in case the app is closed or killed before the end; "finished" once, at the end.
 */
export type SimListener = (data: SimData | null, phase: "progress" | "checkpoint" | "finished") => void;

type WorkerState = "preparing" | "idle" | "running" | "failed";

function randomSeed(): string {
  const seed = new BigUint64Array(1);
  crypto.getRandomValues(seed);
  return seed[0].toString();
}

function errorText(e: unknown): string {
  return e instanceof Error ? e.message : String(e);
}

/** Running time without the time a run spent paused in the background. */
class Stopwatch {
  private total = 0;
  private since: number | null = null;

  start(): void {
    this.since ??= performance.now();
  }

  pause(): void {
    if (this.since === null) return;
    this.total += performance.now() - this.since;
    this.since = null;
  }

  ms(): number {
    return this.total + (this.since === null ? 0 : performance.now() - this.since);
  }

  /** Takes back the time beyond `ms`. */
  limit(ms: number): void {
    this.total -= Math.max(0, this.ms() - ms);
  }
}

/** The run in progress (there is one at a time). */
interface Run {
  job: number;
  req: SimRequest;
  /** Results held before this run. It never changes, so every checkpoint and the end are this plus the run so far. */
  base: SimData | null;
  labels: SimLabels;
  listener: SimListener;
  threads: number;
  /** Tallies of the chunks finished so far. */
  tally: Float64Array;
  states: WorkerState[];
  /** Rounds handed out so far. */
  claimed: number;
  basicEv: number;
  stopping: boolean;
  paused: boolean;
  clock: Stopwatch;
  /** Running time when each worker got the chunk it is working on. */
  since: number[];
  /** The longest a chunk has taken so far. */
  chunkMs: number;
  /** While paused: the running time by which the chunks in progress were due (see `time`). */
  dueMs: number;
  /** Running time at the last checkpoint. */
  checkpointMs: number;
  /** Running time when the last chunk came in: the time the rounds so far took. */
  lastTallyMs: number;
  updateTimer: number;
  drainTimer: number;
}

class Simulation {
  running = $state(false);
  done = $state(0);
  target = $state(0);
  seconds = $state(0);
  error = $state<string | null>(null);
  /** Set when Stop ended the last run early (a sweep then stops too). */
  stoppedByUser = false;

  /** Workers, kept between runs; null where one was lost (a new one starts with the next run). */
  private pool: (Worker | null)[] = [];
  private job = 0;
  private current: Run | null = null;

  constructor() {
    document.addEventListener("visibilitychange", () => this.visibilityChanged());
    // Last chances to keep the rounds so far: the page is closing, or Chrome freezes it in the background.
    const keep = () => {
      if (this.current) this.checkpoint(this.current);
    };
    window.addEventListener("pagehide", keep);
    document.addEventListener("freeze", keep);
  }

  stop(): void {
    const r = this.current;
    if (!r) return;
    r.stopping = true;
    this.stoppedByUser = true;
    this.settle(r);
  }

  /** Runs a fresh simulation to the end (or until stopped) and resolves with its results. */
  run(req: SimRequest, labels: SimLabels): Promise<SimData | null> {
    return new Promise((resolve) =>
      this.start(req, null, labels, (data, phase) => {
        if (phase === "finished") resolve(data);
      }),
    );
  }

  start(req: SimRequest, base: SimData | null, labels: SimLabels, listener: SimListener): void {
    if (this.running) throw new Error("A simulation is already running.");
    const threads = clampThreads(req.threads);
    const r: Run = {
      job: ++this.job,
      req,
      base,
      labels,
      listener,
      threads,
      tally: new Float64Array(TALLY_LEN),
      states: new Array<WorkerState>(threads).fill("preparing"),
      claimed: 0,
      basicEv: Number.NaN,
      stopping: false,
      // A run that starts in the background (the next point of a sweep) waits until the app is back.
      paused: PAUSE_WHEN_HIDDEN && document.visibilityState === "hidden",
      clock: new Stopwatch(),
      since: new Array<number>(threads).fill(0),
      chunkMs: 0,
      dueMs: 0,
      checkpointMs: 0,
      lastTallyMs: 0,
      updateTimer: 0,
      drainTimer: 0,
    };
    this.current = r;
    this.stoppedByUser = false;
    this.running = true;
    this.done = 0;
    this.target = req.rounds;
    this.seconds = 0;
    this.error = null;
    if (!r.paused) r.clock.start();

    try {
      this.resize(threads);
    } catch (e) {
      // No workers here (for example a WebView that refuses them): the run ends at once, with the reason.
      r.states.fill("failed");
      this.error = "The simulation could not start: " + errorText(e);
      this.finish(r);
      return;
    }
    const request = { ...req, threads: undefined, rounds: undefined, seed: randomSeed() };
    this.pool.forEach((w, i) =>
      w?.postMessage({ type: "prepare", job: r.job, request: { ...request, worker: i } } satisfies WorkerRequest),
    );
  }

  private reply(w: Worker, m: WorkerReply): void {
    const r = this.current;
    const i = this.pool.indexOf(w);
    // Replies to an earlier run, or from a worker that was given up, are ignored.
    if (!r || m.job !== r.job || i < 0 || i >= r.threads) return;
    if (m.type === "prepared") {
      r.basicEv = m.basicEv;
      this.assign(r, i);
    } else if (m.type === "tally") {
      for (let k = 0; k < TALLY_LEN; k++) r.tally[k] += m.tally[k];
      this.done += m.rounds;
      r.lastTallyMs = this.time(r);
      if (!r.paused) r.chunkMs = Math.max(r.chunkMs, r.lastTallyMs - r.since[i]);
      this.seconds = r.lastTallyMs / 1000;
      if (r.lastTallyMs - r.checkpointMs >= CHECKPOINT_MS) this.checkpoint(r);
      r.updateTimer ||= setTimeout(() => {
        r.updateTimer = 0;
        if (this.current === r) r.listener(this.results(r), "progress");
      }, UPDATE_MS);
      this.assign(r, i);
    } else {
      this.fail(r, i, m.message);
    }
  }

  /** Gives worker i its next chunk; when there is none (or the run is paused) the worker waits. */
  private assign(r: Run, i: number): void {
    if (!r.stopping && !r.paused && r.claimed < r.req.rounds) {
      const rounds = Math.min(CHUNK, r.req.rounds - r.claimed);
      r.claimed += rounds;
      r.states[i] = "running";
      r.since[i] = r.clock.ms();
      this.pool[i]?.postMessage({ type: "run", job: r.job, rounds } satisfies WorkerRequest);
    } else {
      r.states[i] = "idle";
      this.settle(r);
    }
  }

  /** Worker i failed: the run stops with the first error, the worker is replaced, the others finish their chunks. */
  private fail(r: Run, i: number, message: string): void {
    this.error ??= message;
    r.stopping = true;
    r.states[i] = "failed";
    this.discard(i);
    this.settle(r);
  }

  /** A worker that reported an error by itself (or sent a reply that can't be read) is replaced; its run ends. */
  private lose(w: Worker, message: string): void {
    const i = this.pool.indexOf(w);
    if (i < 0) return;
    const r = this.current;
    if (r && i < r.threads) this.fail(r, i, message);
    else this.discard(i);
  }

  /** After a worker went idle or failed, or Stop: ends the run once no worker is busy, or waits for them. */
  private settle(r: Run): void {
    if (this.current !== r) return;
    const busy = r.states.some((s) => s === "preparing" || s === "running");
    if (r.paused && !busy) {
      // In the background the clock ran on for the chunks in progress: they are all in now.
      this.time(r);
      r.clock.pause();
    }
    if (!r.stopping && r.claimed < r.req.rounds) {
      // Paused in the background with rounds still to do: once every worker is idle, all of their chunks are in.
      if (!busy) this.checkpoint(r);
      return;
    }
    if (busy) this.armDrain(r);
    else this.finish(r);
  }

  /** Once a run hands out no more chunks, it waits a limited time for those in progress (see DRAIN_MS). */
  private armDrain(r: Run): void {
    // An app suspended in the background must not count as hung: the wait starts when it is back.
    if (r.drainTimer || document.visibilityState === "hidden") return;
    r.drainTimer = setTimeout(() => this.giveUp(r), DRAIN_MS);
  }

  /** The chunks in progress took too long: their workers have hung or died. They are replaced, and the run ends. */
  private giveUp(r: Run): void {
    r.drainTimer = 0;
    if (this.current !== r) return;
    let lost = 0;
    r.states.forEach((s, i) => {
      if (s !== "preparing" && s !== "running") return;
      r.states[i] = "failed";
      this.discard(i);
      lost++;
    });
    if (lost > 0)
      this.error ??=
        (lost === 1 ? "A simulation worker" : `${lost} simulation workers`) +
        " stopped answering; the rounds finished before are kept.";
    // The wait for the lost chunks is not simulation time: the rounds kept took until the last chunk came in.
    this.finish(r, r.lastTallyMs);
  }

  /** Ends the run; `ms` is its running time (until now unless given). */
  private finish(r: Run, ms = this.time(r)): void {
    clearTimeout(r.updateTimer);
    clearTimeout(r.drainTimer);
    this.seconds = ms / 1000;
    this.current = null;
    this.running = false;
    r.listener(this.results(r), "finished");
  }

  /** Passes on the results so far to be kept, in case the app is closed or killed before the run ends. */
  private checkpoint(r: Run): void {
    if (this.current !== r) return;
    r.checkpointMs = this.time(r);
    if (this.done === 0) return;
    this.seconds = r.checkpointMs / 1000;
    r.listener(this.results(r), "checkpoint");
  }

  private visibilityChanged(): void {
    const hidden = document.visibilityState === "hidden";
    const r = this.current;
    if (!r) {
      // An idle phone app gives back its workers' memory in the background, where the system closes the apps that
      // use the most; the next run starts new ones.
      if (hidden && PAUSE_WHEN_HIDDEN) this.release();
      return;
    }
    if (hidden) {
      // The app may be closed or killed in the background: keep the rounds so far.
      this.checkpoint(r);
      clearTimeout(r.drainTimer);
      r.drainTimer = 0;
      if (PAUSE_WHEN_HIDDEN && !r.paused) {
        r.paused = true;
        // No more chunks are handed out. Those in progress are due once each has run as long as the longest so far.
        r.dueMs = r.clock.ms();
        r.states.forEach((s, i) => {
          if (s === "running") r.dueMs = Math.max(r.dueMs, r.since[i] + r.chunkMs);
        });
        this.settle(r);
      }
      return;
    }
    if (r.paused) {
      // Chunks still in progress were held up while the app was suspended: that time doesn't count.
      this.time(r);
      r.paused = false;
      r.clock.start();
      r.states.forEach((s, i) => {
        if (s === "idle") this.assign(r, i);
      });
    }
    this.settle(r);
  }

  /**
   * The run's time so far. A phone app in the background hands out no more chunks, but those in progress still finish
   * and their rounds count, so the clock runs on until the last of them is in. It stops at the latest when they were
   * due (each taking as long as the longest chunk so far): beyond that, the system had suspended the app. Before the
   * first chunk of a run is in there is no such measure, and the clock stops with the app.
   */
  private time(r: Run): number {
    if (r.paused) r.clock.limit(r.dueMs);
    return r.clock.ms();
  }

  private results(r: Run): SimData | null {
    return this.done > 0 ? mergeSim(r.base, this.build(r)) : r.base;
  }

  private build(r: Run): SimData {
    const t = r.tally;
    const column = (k: number) => Array.from(t.subarray(k * NUM_BINS, (k + 1) * NUM_BINS));
    return {
      binMin: BIN_MIN,
      numBins: NUM_BINS,
      spots: r.req.spots,
      n: column(0),
      sa: column(1),
      qa: column(2),
      sb: column(3),
      qb: column(4),
      ab: column(5),
      rounds: t[6 * NUM_BINS],
      shoes: t[6 * NUM_BINS + 1],
      emergency: t[6 * NUM_BINS + 2],
      seconds: this.seconds,
      threads: r.threads,
      ...r.labels,
      basicEv: r.basicEv,
      created: Date.now(),
    };
  }

  /** Keeps `n` workers, starting new ones where they are missing. */
  private resize(n: number): void {
    while (this.pool.length > n) this.pool.pop()?.terminate();
    for (let i = 0; i < n; i++) this.pool[i] ??= this.spawn();
  }

  /** A new worker with the compiled engine. Its handlers stay for its whole life, between runs too. */
  private spawn(): Worker {
    const w = new Worker(new URL("./engine/sim-worker.ts", import.meta.url), { type: "module" });
    w.onmessage = (e: MessageEvent<WorkerReply>) => this.reply(w, e.data);
    w.onerror = (e) => {
      // Uncaught in the worker, or its script did not load: nothing more will come from it.
      e.preventDefault();
      this.lose(w, "A simulation worker stopped: " + (e.message || "unknown error"));
    };
    w.onmessageerror = () => this.lose(w, "A simulation worker's reply could not be read.");
    try {
      w.postMessage({ type: "init", module: engineModule() } satisfies WorkerRequest);
    } catch (e) {
      w.terminate();
      throw e;
    }
    return w;
  }

  private discard(i: number): void {
    this.pool[i]?.terminate();
    this.pool[i] = null;
  }

  /** Ends every worker (between runs); the next run starts new ones. */
  private release(): void {
    for (const w of this.pool) w?.terminate();
    this.pool = [];
  }
}

export const simulation = new Simulation();
