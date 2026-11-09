// The engine on the main thread: everything except simulations (those run in Web Workers, see simulation.svelte.ts).
import wasmUrl from "./calcbj.wasm?url";
import type {
  BetRamp,
  BinStat,
  ChartData,
  CountInfo,
  CountSystem,
  Evaluation,
  OptimizerSettings,
  RampResult,
  Rules,
  RulesInfo,
  SimData,
  StrategyConfig,
  StrategyInfo,
} from "./types";
import type { SlowReply, SlowRequest } from "./slow-worker";
import { EngineInstance } from "./wasm";

let module: WebAssembly.Module;
let instance: EngineInstance;
/** Set while a new instance replaces one that trapped. */
let replacing = false;

/** Downloads and compiles the engine; must finish before anything else uses it. */
export async function loadEngine(): Promise<void> {
  try {
    module = await WebAssembly.compileStreaming(fetch(wasmUrl));
  } catch {
    // Some static hosts don't send the application/wasm type that streaming compilation needs.
    module = await WebAssembly.compile(await (await fetch(wasmUrl)).arrayBuffer());
  }
  instance = await EngineInstance.create(module);
}

/** The compiled module, which simulation workers instantiate for themselves. */
export function engineModule(): WebAssembly.Module {
  return module;
}

function call<T>(request: object): T {
  try {
    return instance.call<T>(request);
  } catch (e) {
    // A trap leaves the instance unusable. One new instance replaces it (several calls can fail before it is ready,
    // and each instance costs memory); if even that fails, the next failing call tries again.
    if (instance.broken && !replacing) {
      replacing = true;
      EngineInstance.create(module)
        .then((i) => (instance = i))
        .catch(() => {})
        .finally(() => (replacing = false));
    }
    throw e;
  }
}

/** What basic strategy and its expectation depend on: the rules without how deep the shoe is dealt. */
function basicKey(rules: Rules): string {
  return JSON.stringify({ ...rules, penetration: 0, burnCard: false });
}

/** House edges and effects of removal worked out so far, by `basicKey`. */
const edges = new Map<string, number>();
const effects = new Map<string, number[]>();
/** The effects of removal being worked out (one set of rules at a time). */
let effectsJob: { key: string; promise: Promise<number[]>; cancel: () => void } | null = null;

/**
 * Engine requests that take too long for the main thread run in a worker of their own. `onValue` gets each answer as
 * soon as it is in; the promise resolves with all of them. The worker is ended after the last answer, or by `cancel`
 * (the promise is rejected then).
 */
function runSlow(requests: object[], onValue?: (index: number, value: unknown) => void) {
  // As JSON text: the requests may hold reactive proxies, which can't be passed to a worker as they are.
  const texts = requests.map((r) => JSON.stringify(r));
  let worker: Worker;
  try {
    worker = new Worker(new URL("./slow-worker.ts", import.meta.url), { type: "module" });
  } catch {
    // No workers here (a WebView that refuses them): on the main thread, then.
    const promise = Promise.resolve().then(() =>
      texts.map((t, i) => {
        const value = call<unknown>(JSON.parse(t));
        onValue?.(i, value);
        return value;
      }),
    );
    promise.catch(() => {});
    return { promise, cancel: () => {} };
  }
  let cancel = () => {};
  const promise = new Promise<unknown[]>((resolve, reject) => {
    const values: unknown[] = new Array(texts.length).fill(null);
    let live = true;
    const end = () => {
      live = false;
      worker.terminate();
    };
    cancel = () => {
      end();
      reject(new Error("Not needed any more."));
    };
    worker.onmessage = (e: MessageEvent<SlowReply>) => {
      const m = e.data;
      if (!live) return;
      if ("index" in m) {
        values[m.index] = m.value;
        onValue?.(m.index, m.value);
        return;
      }
      end();
      if ("error" in m) reject(new Error(m.error));
      else resolve(values);
    };
    worker.onerror = (e) => {
      e.preventDefault();
      end();
      reject(new Error(e.message || "The engine worker stopped."));
    };
    worker.postMessage({ module, requests: texts } satisfies SlowRequest);
  });
  // A caller that has gone away must not leave an unhandled rejection behind.
  promise.catch(() => {});
  return { promise, cancel };
}

export const engine = {
  /** Player expectation off the top with total-dependent basic strategy (negative = house edge). */
  houseEdge(rules: Rules): number {
    const key = basicKey(rules);
    let ev = edges.get(key);
    if (ev === undefined) edges.set(key, (ev = call<number>({ op: "houseEdge", rules })));
    return ev;
  },
  /**
   * The same for several sets of rules (the Game page's list of rule changes): about a second for twenty of them, so a
   * worker does it. `known` holds those worked out before (null for the others); `onUpdate` gets the list again each
   * time another one is in, and with NaN for those that could not be worked out. `cancel` drops the work.
   */
  houseEdges(list: Rules[], onUpdate: (values: (number | null)[]) => void) {
    const keys = list.map(basicKey);
    const known = keys.map((k) => edges.get(k) ?? null);
    const missing = known.flatMap((v, i) => (v === null ? [i] : []));
    if (missing.length === 0) return { known, cancel: () => {} };
    const values = [...known];
    const job = runSlow(
      missing.map((i) => ({ op: "houseEdge", rules: list[i] })),
      (k, value) => {
        edges.set(keys[missing[k]], value as number);
        values[missing[k]] = value as number;
        onUpdate([...values]);
      },
    );
    let cancelled = false;
    job.promise.catch(() => {
      if (!cancelled) onUpdate(values.map((v) => v ?? Number.NaN));
    });
    return {
      known,
      cancel: () => {
        cancelled = true;
        job.cancel();
      },
    };
  },
  rulesInfo: (rules: Rules, spots: number) => call<RulesInfo>({ op: "rulesInfo", rules, spots }),
  presets: () => call<CountSystem[]>({ op: "presets" }),
  countKey: (count: CountSystem) => call<string>({ op: "countKey", count }),
  /** `eor`: the rules' effects of removal (`effectsOfRemoval`), or null while they are not in: no betting correlation then. */
  countInfo: (rules: Rules, count: CountSystem, eor: number[] | null) =>
    call<CountInfo>({ op: "countInfo", rules, count, eor }),
  /** The effects of removal for these rules if they have been worked out already, else null. */
  knownEffects: (rules: Rules): number[] | null => effects.get(basicKey(rules)) ?? null,
  /**
   * The effects of removal of each rank ([1..10]) for basic strategy under these rules. Ten exact evaluations take
   * about a second (more on a phone), so a worker does them, once per set of rules. Asking for other rules meanwhile
   * drops the work in progress.
   */
  effectsOfRemoval(rules: Rules): Promise<number[]> {
    const key = basicKey(rules);
    const known = effects.get(key);
    if (known) return Promise.resolve(known);
    if (effectsJob?.key !== key) {
      effectsJob?.cancel();
      const job = runSlow([{ op: "effectsOfRemoval", rules }]);
      const promise = job.promise.then(([value]) => {
        effects.set(key, value as number[]);
        return value as number[];
      });
      promise.catch(() => {});
      effectsJob = { key, promise, cancel: job.cancel };
    }
    return effectsJob.promise;
  },
  /** Identifies the simulation for these settings (results are kept per key). */
  simKey: (rules: Rules, count: CountSystem, config: StrategyConfig, spots: number) =>
    call<string>({ op: "simKey", rules, count, config, spots }),
  strategy: (rules: Rules, count: CountSystem, config: StrategyConfig) =>
    call<StrategyInfo>({ op: "strategy", rules, count, config }),
  /** Strategy chart at a count value, or basic strategy for null. */
  chart: (rules: Rules, count: CountSystem, config: StrategyConfig, at: number | null) =>
    call<ChartData>({ op: "chart", rules, count, config, at }),
  /** Ramp rows that suit the count, filled from a preset (keeps the old bets where rows overlap). */
  defaultRamp: (rules: Rules, count: CountSystem, ramp: BetRamp | null, preset: number) =>
    call<RampResult>({ op: "defaultRamp", rules, count, ramp, preset }),
  applyPreset: (rules: Rules, count: CountSystem, ramp: BetRamp, preset: number) =>
    call<RampResult>({ op: "applyPreset", rules, count, ramp, preset }),
  indexRange: (rules: Rules, count: CountSystem) => call<{ min: number; max: number }>({ op: "indexRange", rules, count }),
  /**
   * The ramp moved from the count and rules it was set for to others: a preset is applied again over the new range; a
   * custom ramp is carried over row by row, each new row taking the bet of the old row with the nearest Hi-Lo equivalent.
   */
  carryRamp: (from: Rules, fromCount: CountSystem, rules: Rules, count: CountSystem, ramp: BetRamp, preset: number) =>
    call<RampResult>({ op: "carryRamp", from, fromCount, rules, count, ramp, preset }),
  /** Published Hi-Lo index numbers in this count's own scale (unchanged for a count that reads like Hi-Lo). */
  fromHiLo: (rules: Rules, count: CountSystem, values: number[]) =>
    call<number[]>({ op: "fromHiLo", rules, count, values }),
  /** The ramp for other rules (another number of decks): a preset again, or a custom ramp row by row by Hi-Lo equivalent. */
  rampForRules: (from: Rules, rules: Rules, count: CountSystem, ramp: BetRamp, preset: number) =>
    call<BetRamp>({ op: "rampForRules", from, rules, count, ramp, preset }),
  bins: (sim: SimData) => call<BinStat[]>({ op: "bins", sim }),
  evaluate: (sim: SimData, ramp: BetRamp, unit: number, rph: number, bankroll: number) =>
    call<Evaluation>({ op: "evaluate", sim, ramp, unit, rph, bankroll }),
  optimize: (sim: SimData, ramp: BetRamp, settings: OptimizerSettings) =>
    call<BetRamp>({ op: "optimize", sim, ramp, settings }),
  /** Standard normal CDF. */
  phi: (x: number[]) => call<number[]>({ op: "phi", x }),
  /** Inverse standard normal CDF (null for 0 and 1, which are infinite). */
  phiInv: (p: number[]) => call<(number | null)[]>({ op: "phiInv", p }),
  /** Chance of losing the whole bankroll within n rounds (per-round mean and variance in dollars). */
  tripRuin: (mu: number, variance: number, bankroll: number, n: number) =>
    call<number>({ op: "tripRuin", mu, var: variance, bankroll, n }),
  /** Chance of being behind after n rounds. */
  probLoss: (mu: number, variance: number, n: number) => call<number>({ op: "probLoss", mu, var: variance, n }),
};
