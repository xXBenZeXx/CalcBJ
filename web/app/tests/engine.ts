// The app's modules with the real engine in the test environment. `npm test` builds src/lib/engine/calcbj.wasm first
// (npm run wasm); its download is answered from that file, as the browser gets it from the server.
import { readFileSync } from "node:fs";
import { join } from "node:path";
import { vi } from "vitest";
import type { SimData } from "../src/lib/engine/types";
import { NUM_BINS } from "../src/lib/engine/wasm";

/** A file of the app (web/app, where the tests run): jsdom's URL objects aren't paths to Node. */
export function appFile(path: string): string {
  return join(process.cwd(), path);
}

const wasm = readFileSync(appFile("src/lib/engine/calcbj.wasm"));
const realFetch = globalThis.fetch;
globalThis.fetch = (async (input: RequestInfo | URL, init?: RequestInit) => {
  const url = input instanceof Request ? input.url : String(input);
  if (url.endsWith("calcbj.wasm")) return new Response(wasm, { headers: { "Content-Type": "application/wasm" } });
  return realFetch(input, init);
}) as typeof fetch;

/**
 * A new copy of the app's state, as the app starts with `storage` in localStorage: the engine is loaded first, as
 * the app's start does, and the state reads its settings and stored results when it is imported.
 */
export async function freshApp(storage: Record<string, string> = {}) {
  vi.resetModules();
  localStorage.clear();
  for (const [key, value] of Object.entries(storage)) localStorage.setItem(key, value);
  const { loadEngine } = await import("../src/lib/engine/engine");
  await loadEngine();
  return import("../src/lib/state.svelte");
}

/** Results as the app stores them: tallies for `rounds` rounds (all zero) under `key`. */
export function storedSim(key: string, rounds: number): SimData {
  const zeros = () => new Array<number>(NUM_BINS).fill(0);
  return {
    binMin: -100,
    numBins: NUM_BINS,
    spots: 3,
    n: zeros(),
    sa: zeros(),
    qa: zeros(),
    sb: zeros(),
    qb: zeros(),
    ab: zeros(),
    rounds,
    shoes: Math.ceil(rounds / 60),
    emergency: 0,
    seconds: 1,
    threads: 4,
    key,
    rulesText: "6 decks, H17",
    countText: "Hi-Lo",
    strategyText: "I18 + Fab 4",
    metric: "TrueCount",
    basicEv: -0.005,
    created: 0,
  };
}
