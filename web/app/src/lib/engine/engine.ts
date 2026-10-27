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
import { EngineInstance } from "./wasm";

let module: WebAssembly.Module;
let instance: EngineInstance;

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
    if (instance.broken) void EngineInstance.create(module).then((i) => (instance = i));
    throw e;
  }
}

export const engine = {
  /** Player expectation off the top with total-dependent basic strategy (negative = house edge). */
  houseEdge: (rules: Rules) => call<number>({ op: "houseEdge", rules }),
  rulesInfo: (rules: Rules, spots: number) => call<RulesInfo>({ op: "rulesInfo", rules, spots }),
  presets: () => call<CountSystem[]>({ op: "presets" }),
  countKey: (count: CountSystem) => call<string>({ op: "countKey", count }),
  countInfo: (rules: Rules, count: CountSystem) => call<CountInfo>({ op: "countInfo", rules, count }),
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
